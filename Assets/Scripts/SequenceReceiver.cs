using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

// Connects to the Python backend (VoxignPipeline.py) and plays each new test_sequence.json as soon as it's delivered
[RequireComponent(typeof(SignPlayer))]
public class SequenceReceiver : MonoBehaviour
{   
    // Connection properties
    [SerializeField] private string serverUrl = "ws://127.0.0.1:8765";
    [SerializeField] private float reconnectDelay = 2f;

    [Serializable]
    private class BackendMessage
    {
        public string type;
        public string english;
        public string gloss;
    }

    private SignPlayer player;
    private CancellationTokenSource cancellation;
    private readonly ConcurrentQueue<LandmarkSequence> received = new ConcurrentQueue<LandmarkSequence>();

    void Start()
    {
        player = GetComponent<SignPlayer>();
        // Cancellation token for stopping the background task when the object is destroyed
        cancellation = new CancellationTokenSource();

        // Unity APIs must be read on the main thread, so these values must be captured here first before going to the background
        string sequencePath = Path.Combine(Application.dataPath, "test_sequence.json");
        Uri uri = new Uri(serverUrl);
        TimeSpan delay = TimeSpan.FromSeconds(reconnectDelay);
        CancellationToken token = cancellation.Token;

        // Move them off the main thread so file reads and JSON parsing never stall frame playback
        Task.Run(() => ListenLoop(uri, sequencePath, delay, token));
    }

    // Update enqueued sign sequences for playback
    void Update()
    {
        while (received.TryDequeue(out LandmarkSequence sequence))
        {
            foreach (LandmarkSeries sign in sequence.signs) player.Enqueue(sign);
        }
    }

    void OnDestroy()
    {
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
    }


    private async Task ListenLoop(Uri uri, string sequencePath, TimeSpan delay, CancellationToken token)
    {   
        // Buffer to prevent losing signs as new ones come in
        byte[] buffer = new byte[4096];
        bool loggedWaiting = false;
        
        // Main loop for maintaining the WebSocket connection and receiving messages
        while (!token.IsCancellationRequested)
        {
            using (ClientWebSocket socket = new ClientWebSocket())
            {
                try
                {   
                    // "uri" is not a typo, but the Websocket-defined format of the address of to connect to
                    await socket.ConnectAsync(uri, token);
                    Debug.Log($"[SequenceReceiver] Connected to backend at {uri}.");
                    loggedWaiting = false;

                    // Constantly listen for new messages (test_sequence.json updates) and handle them
                    while (socket.State == WebSocketState.Open)
                    {
                        string message = await ReceiveMessage(socket, buffer, token);
                        if (message == null) break;
                        HandleMessage(message, sequencePath);
                    }

                    Debug.Log("[SequenceReceiver] Backend disconnected.");
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (WebSocketException)
                {
                    if (!loggedWaiting)
                    {
                        Debug.Log($"[SequenceReceiver] Waiting for backend at {uri}...");
                        loggedWaiting = true;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[SequenceReceiver] {e.GetType().Name}: {e.Message}");
                }
            }

            try
            {
                await Task.Delay(delay, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
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
        if (notification == null || notification.type != "sequence") return;

        LandmarkSequence sequence;
        try
        {   // Convert the entire JSON file into a LandmarkSequence
            sequence = LandmarkSequence.FromJson(File.ReadAllText(sequencePath));
        }
        catch (IOException e)
        {
            Debug.LogWarning($"[SequenceReceiver] Could not read {sequencePath}: {e.Message}");
            return;
        }

        if (sequence == null)
        {
            Debug.LogWarning("[SequenceReceiver] New sequence contained no playable frames.");
            return;
        }

        // Log the received sequence and queue it up for processing
        Debug.Log($"[SequenceReceiver] \"{notification.english}\" -> {notification.gloss} ({sequence.signs.Count} sign(s))");
        received.Enqueue(sequence);
    }
}
