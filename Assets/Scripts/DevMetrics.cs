using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

// Development-only measurement of the path from the last audio chunk to the first rendered sign,
// plus the frame rate during playback and the length of any reconnect outage.
// Results are appended to dev_metrics.csv in Application.persistentDataPath so a run can be
// summarised without a profiler. Nothing is compiled into a release build.
[RequireComponent(typeof(SequenceReceiver), typeof(SignPlayer))]
public class DevMetrics : MonoBehaviour
{
    private const string FileName = "dev_metrics.csv";
    private const string Header = "timestamp,event,english,speech_to_payload_ms,payload_to_render_ms,avg_fps,low_fps,reconnect_ms";

    private SequenceReceiver receiver;
    private SignPlayer player;
    private VoiceAudioStreamer streamer;

    // Armed when a sequence arrives; cleared by the first SignStarted for that sequence
    private bool awaitingSign;
    private float sequenceAt;
    private float speechAt = -1f;
    private string transcript = string.Empty;
    private float pendingSpeechMs = -1f;
    private float pendingRenderMs;

    // Frame sampling while the avatar is playing
    private bool wasPlaying;
    private int frames;
    private float playbackStart;
    private float worstFrame;

    private int utterances;
    private float renderSum;
    private float averageFpsSum;
    private string filePath;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (!Application.isEditor && !Debug.isDebugBuild) return;

        // The avatar scene is loaded additively, so attach whenever a scene appears rather than once
        SceneManager.sceneLoaded += (scene, mode) => TryAttach();
        TryAttach();
    }

    private static void TryAttach()
    {
        SignPlayer avatar = FindAnyObjectByType<SignPlayer>();
        if (avatar == null) return;
        if (avatar.GetComponent<SequenceReceiver>() == null) return;
        if (avatar.GetComponent<DevMetrics>() == null) avatar.gameObject.AddComponent<DevMetrics>();
    }
#endif

    void Awake()
    {
        receiver = GetComponent<SequenceReceiver>();
        player = GetComponent<SignPlayer>();
        streamer = GetComponent<VoiceAudioStreamer>();
        filePath = Path.Combine(Application.persistentDataPath, FileName);
    }

    void OnEnable()
    {
        // Second guard for a component left in a non-development scene by mistake
        if (!Application.isEditor && !Debug.isDebugBuild)
        {
            enabled = false;
            return;
        }

        if (receiver != null) receiver.SequenceReceived += OnSequenceReceived;
        if (player != null) player.SignStarted += OnSignStarted;
        if (receiver != null) receiver.Reconnected += OnReconnected;
    }

    void OnDisable()
    {
        if (receiver != null) receiver.SequenceReceived -= OnSequenceReceived;
        if (player != null) player.SignStarted -= OnSignStarted;
        if (receiver != null) receiver.Reconnected -= OnReconnected;
    }

    void OnApplicationQuit()
    {
        if (utterances == 0) return;

        Debug.Log($"[DevMetrics] {utterances} utterance(s); mean payload-to-render " +
                  $"{renderSum / utterances:F0} ms; mean playback {averageFpsSum / utterances:F1} fps. File: {filePath}");
    }

    // A sequence has arrived: arm the measurement for the next sign that starts
    private void OnSequenceReceived()
    {
        awaitingSign = true;
        sequenceAt = receiver.LastSequenceReceivedAt;
        transcript = receiver.LastSequenceEnglish ?? string.Empty;
        speechAt = streamer != null && streamer.LastAudioSentAt > 0f ? streamer.LastAudioSentAt : -1f;
    }

    // The first sign of the utterance has begun rendering
    private void OnSignStarted(string gloss)
    {
        if (!awaitingSign) return;
        awaitingSign = false;

        float renderMs = (Time.realtimeSinceStartup - sequenceAt) * 1000f;
        float speechMs = speechAt > 0f ? (sequenceAt - speechAt) * 1000f : -1f;

        utterances++;
        renderSum += renderMs;
        pendingSpeechMs = speechMs;
        pendingRenderMs = renderMs;

        Debug.Log($"[DevMetrics] payload-to-render {renderMs:F0} ms" +
                  (speechMs >= 0f ? $", last audio chunk to payload {speechMs:F0} ms" : string.Empty) +
                  $" (\"{transcript}\" -> {gloss}).");
    }

    void Update()
    {
        if (player == null) return;

        if (player.IsPlaying)
        {
            if (!wasPlaying)
            {
                wasPlaying = true;
                frames = 0;
                worstFrame = 0f;
                playbackStart = Time.realtimeSinceStartup;
            }

            frames++;
            if (Time.unscaledDeltaTime > worstFrame) worstFrame = Time.unscaledDeltaTime;
        }
        else if (wasPlaying)
        {
            wasPlaying = false;
            float elapsed = Time.realtimeSinceStartup - playbackStart;
            float average = elapsed > 0f ? frames / elapsed : 0f;
            float low = worstFrame > 0f ? 1f / worstFrame : 0f;

            averageFpsSum += average;
            WriteRow("utterance", transcript, pendingSpeechMs, pendingRenderMs, average, low, -1f);
            Debug.Log($"[DevMetrics] playback {average:F1} fps average, {low:F1} fps low, " +
                      $"{frames} frames in {elapsed:F2} s.");
            pendingSpeechMs = -1f;
            pendingRenderMs = 0f;
        }
    }

    private void OnReconnected(float gapSeconds)
    {
        Debug.Log($"[DevMetrics] reconnect gap {gapSeconds:F2} s (NFR-03 target 5 s).");
        WriteRow("reconnect", string.Empty, -1f, -1f, -1f, -1f, gapSeconds * 1000f);
    }

    private void WriteRow(string kind, string english, float speechMs, float renderMs, float averageFps, float lowFps, float reconnectMs)
    {
        try
        {
            bool newFile = !File.Exists(filePath);
            StringBuilder row = new StringBuilder();
            if (newFile) row.AppendLine(Header);

            row.Append(DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)).Append(',');
            row.Append(kind).Append(',');
            row.Append('"').Append((english ?? string.Empty).Replace("\"", "'")).Append('"').Append(',');
            row.Append(Format(speechMs)).Append(',');
            row.Append(Format(renderMs)).Append(',');
            row.Append(Format(averageFps)).Append(',');
            row.Append(Format(lowFps)).Append(',');
            row.Append(Format(reconnectMs)).AppendLine();

            File.AppendAllText(filePath, row.ToString(), Encoding.UTF8);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[DevMetrics] Could not write {filePath}: {e.Message}");
        }
    }

    private static string Format(float value)
    {
        return value < 0f ? string.Empty : value.ToString("F1", CultureInfo.InvariantCulture);
    }
}
