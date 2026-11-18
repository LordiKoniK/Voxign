using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;

[Serializable]
public class TranscriptionEvent : UnityEvent<string> { }

// Connects to the Python backend (VoxignPipeline.py) and plays each sequence it sends
// When the backend runs on the same machine (DEV_MODE), it falls back to reading test_sequence.json
[RequireComponent(typeof(SignPlayer))]
public class SequenceReceiver : MonoBehaviour
{
    // Development endpoint for connecting to the backend when running the whole project locally
    [SerializeField] private string serverUrl = "ws://127.0.0.1:8765";

    // Public TLS-secured backend endpoint used by mobile clients (will update to a valid url once I get a domain)
    [SerializeField] private string productionServerUrl = "wss://voxign.example.com/ws";

    // Whether to use the production URL or the development one
    [SerializeField] private bool useProductionUrlOnDevice = true;

    // Authentication token for connecting to the backend (get from deployment env)
    [SerializeField] private string authToken = "";

    // Connection settings
    [Header("Connection")]
    [SerializeField] private float reconnectDelay = 2f;
    [SerializeField] private float maxReconnectDelay = 30f;
    [SerializeField] private int keepAliveSeconds = 25;

    // Event callback for when the backend sends transcriptions
    [Header("Events")]
    public TranscriptionEvent onTranscription = new TranscriptionEvent();

    // Testing-focused metrics
    // Raised on the main thread when a decoded sequence is handed to the player
    public event Action SequenceReceived;
    // Raised on the main thread with the outage length after an interrupted connection is restored
    public event Action<float> Reconnected;
    // Timestamps to track time between signs/sequences
    public float LastSequenceReceivedAt { get; private set; }
    public string LastSequenceEnglish { get; private set; }

    // Identifier for this run's conversation; generated once and reused across reconnects
    public string SessionKey { get { return sessionKey; } }

    // LIST OF DATA STRUCTURES (all corresponding to JSON format)

    // Data structure for messages received from the backend
    [Serializable]
    private class BackendMessage
    {
        public string type;
        public string sessionKey;
        public int utterance;
        public bool resumed;
        public int ttlSeconds;
        public string english;
        public string gloss;
        public string payload;
        public string payloadEncoding;
    }

    // Data structure for the initial handshake message sent to the backend
    [Serializable]
    private class HelloMessage
    {
        public string type;
        public string client;
        public string token;
        public string sessionKey;
    }

    // Data structure for sequences received from the backend's payload
    private class IncomingSequence
    {
        public LandmarkSequence sequence;
        public string english;
    }

    // Data structure for outgoing audio frames to the backend
    private struct OutgoingFrame
    {
        public byte[] data;
        public bool isText;
    }

    private SignPlayer player;
    private CancellationTokenSource cancellation;
    private readonly ConcurrentQueue<IncomingSequence> received = new ConcurrentQueue<IncomingSequence>();
    private readonly ConcurrentQueue<float> reconnections = new ConcurrentQueue<float>();
    private readonly ConcurrentQueue<OutgoingFrame> outbox = new ConcurrentQueue<OutgoingFrame>();
    private readonly SemaphoreSlim outboxSignal = new SemaphoreSlim(0);
    private readonly System.Random jitter = new System.Random();
    private volatile bool connected;
    private string sessionKey;
    private bool shutdownStarted;
    private static string processSessionKey;

    public bool IsConnected { get { return connected; } }

    void Awake()
    {
        // One key per app launch: static for the life of the process, then discarded with it.
        // It is never written to disk, so the next launch always starts a clean session.
        if (string.IsNullOrEmpty(processSessionKey))
            processSessionKey = Guid.NewGuid().ToString("N");
        sessionKey = processSessionKey;
    }

    void Start()
    {
        player = GetComponent<SignPlayer>();
        // Cancellation token for stopping the background task when the object is destroyed
        cancellation = new CancellationTokenSource();

        string url = ResolveServerUrl();
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
        {
            Debug.LogError($"[SequenceReceiver] Invalid backend URL '{url}'.");
            return;
        }

        Debug.Log($"[SequenceReceiver] Using backend {uri} (device={Application.isMobilePlatform}), session {ShortKey}.");

        // Unity APIs must be read on the main thread, so these values must be captured here first before going to the background
        string sequencePath = Path.Combine(Application.dataPath, "test_sequence.json");
        CancellationToken token = cancellation.Token;

        // Move them off the main thread so file reads and JSON parsing never stall frame playback
        Task.Run(() => ListenLoop(uri, sequencePath, reconnectDelay, maxReconnectDelay, token));
    }

    // Decide which backend URL to use (actual server or local) based on platform (prod must be a mobile device) and the earlier variable
    string ResolveServerUrl()
    {
        if (useProductionUrlOnDevice && Application.isMobilePlatform && !string.IsNullOrEmpty(productionServerUrl))
            return productionServerUrl;
        return serverUrl;
    }

    // Update enqueued sign sequences for playback
    void Update()
    {
        while (received.TryDequeue(out IncomingSequence item))
        {
            if (item.sequence != null)
            {
                // The transcript rides with the first sign so the UI can stay in step with playback
                for (int i = 0; i < item.sequence.signs.Count; i++)
                    player.Enqueue(item.sequence.signs[i], i == 0 ? item.english : null);
            }
            if (!string.IsNullOrEmpty(item.english)) onTranscription.Invoke(item.english);

            LastSequenceReceivedAt = Time.realtimeSinceStartup;
            LastSequenceEnglish = item.english;
            SequenceReceived?.Invoke();
        }

        while (reconnections.TryDequeue(out float gapSeconds))
        {
            Debug.Log($"[SequenceReceiver] Reconnected after {gapSeconds:F2} s.");
            Reconnected?.Invoke(gapSeconds);
        }
    }

    void OnDestroy()
    {
        EndSession();
        if (cancellation != null)
        {
            // Give the queued session_end a moment to leave before stopping the connection
            if (shutdownStarted) cancellation.CancelAfter(400);
            else cancellation.Cancel();
        }
        cancellation = null;
    }

    void OnApplicationQuit()
    {
        EndSession();
    }

    // Tells the backend this run is over so the session key is released immediately
    private void EndSession()
    {
        if (shutdownStarted) return;
        shutdownStarted = true;

        if (SendControl("{\"type\":\"session_end\"}"))
            Debug.Log($"[SequenceReceiver] Ending session {ShortKey}.");
    }

    // Short form used in logs; the full key is only sent to the backend
    private string ShortKey
    {
        get { return string.IsNullOrEmpty(sessionKey) ? "unknown" : sessionKey.Substring(0, Math.Min(8, sessionKey.Length)); }
    }

    // Queues a PCM16 audio chunk for the backend (WebSocket)
    // The data is copied because callers reuse their buffers
    public bool SendAudio(byte[] pcm16)
    {
        if (!connected || pcm16 == null || pcm16.Length == 0) return false;

        byte[] copy = new byte[pcm16.Length];
        Buffer.BlockCopy(pcm16, 0, copy, 0, pcm16.Length);
        Enqueue(new OutgoingFrame { data = copy, isText = false });
        return true;
    }

    // Queues a JSON control message (hello/audio_config/audio_end)
    public bool SendControl(string json)
    {
        if (!connected || string.IsNullOrEmpty(json)) return false;

        Enqueue(new OutgoingFrame { data = Encoding.UTF8.GetBytes(json), isText = true });
        return true;
    }

    // Adds an outgoing audio frame to the queue for sending to backend
    private void Enqueue(OutgoingFrame frame)
    {
        outbox.Enqueue(frame);
        outboxSignal.Release();
    }

    // Clears all queued outgoing frames and resets the signal
    private void ClearOutbox()
    {
        while (outbox.TryDequeue(out _)) { }
        while (outboxSignal.Wait(0)) { }
    }

    // Build the handshake message to initiate connection with backend
    private static byte[] BuildHello(string token, string key)
    {
        HelloMessage hello = new HelloMessage
        {
            type = "hello",
            client = "unity",
            token = token ?? string.Empty,
            sessionKey = key ?? string.Empty,
        };
        return Encoding.UTF8.GetBytes(JsonUtility.ToJson(hello));
    }

    // Maintains the websocket connection, receives results and pumps queued audio/control frames out
    private async Task ListenLoop(Uri uri, string sequencePath, float baseDelay, float maxDelay, CancellationToken token)
    {
        // Buffer to prevent losing signs as new ones come in
        byte[] buffer = new byte[4096];
        bool loggedWaiting = false;
        bool everConnected = false;
        int failures = 0;
        DateTime? lostAt = null;

        while (!token.IsCancellationRequested)
        {
            using (ClientWebSocket ws = new ClientWebSocket())
            using (CancellationTokenSource connection = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                Task sendTask = null;
                try
                {
                    ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(Mathf.Max(5, keepAliveSeconds));

                    // "uri" is not a typo, but the Websocket-defined format of the address of to connect to
                    await ws.ConnectAsync(uri, token);
                    connected = true;
                    failures = 0;
                    ClearOutbox();

                    // Report how long an established connection was down; the initial attempts are not counted
                    if (everConnected && lostAt.HasValue)
                    {
                        reconnections.Enqueue((float)(DateTime.UtcNow - lostAt.Value).TotalSeconds);
                    }
                    everConnected = true;
                    lostAt = null;
                    Debug.Log($"[SequenceReceiver] Connected to backend at {uri}.");
                    loggedWaiting = false;

                    await ws.SendAsync(new ArraySegment<byte>(BuildHello(authToken, sessionKey)), WebSocketMessageType.Text, true, connection.Token);
                    sendTask = SendLoop(ws, connection.Token);

                    // Constantly listen for new messages (transcripts and landmark sequences) and handle them
                    while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                    {
                        string message = await ReceiveMessage(ws, buffer, token);
                        if (message == null) break;
                        HandleMessage(message, sequencePath);
                    }

                    Debug.Log("[SequenceReceiver] Backend disconnected.");
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (WebSocketException e)
                {
                    if (!loggedWaiting)
                    {
                        Debug.Log($"[SequenceReceiver] Waiting for backend at {uri}... ({e.Message})");
                        loggedWaiting = true;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[SequenceReceiver] {e.GetType().Name}: {e.Message}");
                }
                finally
                {
                    if (connected) lostAt = DateTime.UtcNow;
                    connected = false;
                    connection.Cancel();
                    if (sendTask != null)
                    {
                        try { await sendTask; }
                        catch (OperationCanceledException) { }
                    }
                }
            }

            // Exponential backoff with jitter before retrying connection
            failures++;
            double delaySeconds = Math.Min(baseDelay * Math.Pow(2, Math.Min(failures - 1, 5)), maxDelay);
            delaySeconds *= 0.85 + jitter.NextDouble() * 0.3;

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // Drains the outgoing queue in order (ClientWebSocket only allows one send at a time)
    private async Task SendLoop(ClientWebSocket ws, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                await outboxSignal.WaitAsync(token);
                while (outbox.TryDequeue(out OutgoingFrame frame))
                {
                    await ws.SendAsync(
                        new ArraySegment<byte>(frame.data),
                        frame.isText ? WebSocketMessageType.Text : WebSocketMessageType.Binary,
                        true, token);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Debug.LogWarning($"[SequenceReceiver] Send failed: {e.GetType().Name}: {e.Message}");
        }
    }

    // Receives a single message from the WebSocket and returns it as a string (or null if the connection is closed)
    // Designed to account for message fragmentation and ensures the entire message is received before returning anything
    // Returns null when the server closes the connection for clean task killing
    private static async Task<string> ReceiveMessage(ClientWebSocket socket, byte[] buffer, CancellationToken token)
    {
        using (MemoryStream stream = new MemoryStream())
        {
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);

                // If the received message is a close command, close the output and return null to indicate the connection is closed
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (result.CloseStatus.HasValue)
                    {
                        Debug.LogWarning($"[SequenceReceiver] Backend closed the connection: {result.CloseStatus} {result.CloseStatusDescription}");
                    }
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                    return null;
                }
                stream.Write(buffer, 0, result.Count);
            }

            // Continue receiving until the entire message is received (EndOfMessage = true)
            while (!result.EndOfMessage);
            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }
    }

    // Handles an incoming message by parsing it and enqueuing the corresponding landmark sequence
    private void HandleMessage(string message, string sequencePath)
    {
        BackendMessage notification = JsonUtility.FromJson<BackendMessage>(message);
        if (notification == null) return;

        // The server confirms the session key after the handshake
        if (notification.type == "session")
        {
            Debug.Log($"[SequenceReceiver] Session {ShortKey} accepted (resumed={notification.resumed}, ttl={notification.ttlSeconds}s).");
            return;
        }

        // Remote clients receive the landmark data in the JSON payload
        // DEV_MODE clients simply get a notification of the updated file (type != sequence) since everything is local
        if (notification.type != "sequence") return;

        // If the notif type is a sequence, decode it to get the Landmarks
        LandmarkSequence sequence = DecodeSequence(notification.payload, notification.payloadEncoding);

        // Decode error handling fallbacks
        if (sequence == null)
        {
            try
            {   // Attempt to convert the entire JSON file into a LandmarkSequence just in case
                sequence = LandmarkSequence.FromJson(File.ReadAllText(sequencePath));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SequenceReceiver] Could not read {sequencePath}: {e.Message}");
                return;
            }
        }

        if (sequence == null)
        {
            Debug.LogWarning("[SequenceReceiver] New sequence contained no playable frames.");
            return;
        }

        // Log the received sequence and queue it up for processing on the main thread
        Debug.Log($"[SequenceReceiver] \"{notification.english}\" -> {notification.gloss} ({sequence.signs.Count} sign(s))");
        received.Enqueue(new IncomingSequence { sequence = sequence, english = notification.english });
    }

    // Pretty sure I don't need to explain what this does
    // We're encoding the JSON data to improve transmission speed and thus minimize audio input -> sign output overall latency
    // A raw landmark JSON file of a few words can easily get up to hundreds of KB
    private static LandmarkSequence DecodeSequence(string payload, string encoding)
    {
        if (string.IsNullOrEmpty(payload)) return null;

        if (string.IsNullOrEmpty(encoding) || encoding == "raw")
            return LandmarkSequence.FromJson(payload);

        // Ensure to match our encoding format
        if (encoding != "gzip+base64")
        {
            Debug.LogWarning($"[SequenceReceiver] Unknown payload encoding '{encoding}'.");
            return null;
        }

        try
        {
            byte[] compressed = Convert.FromBase64String(payload);
            using (MemoryStream input = new MemoryStream(compressed))
            using (GZipStream gzip = new GZipStream(input, CompressionMode.Decompress))
            using (StreamReader reader = new StreamReader(gzip, Encoding.UTF8))
            {
                return LandmarkSequence.FromJson(reader.ReadToEnd());
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SequenceReceiver] Could not decode sequence payload: {e.Message}");
            return null;
        }
    }
}
