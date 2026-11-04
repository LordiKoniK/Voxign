using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Android;
using UnityEngine.Events;

[Serializable]
public class StreamingStateEvent : UnityEvent<bool> { }

// Captures this device's microphone and streams 16-bit PCM to the backend over the websocket connection in SequenceReceiver
// Works on Android (runtime permission + RECORD_AUDIO manifest entry) and (supposedly) in the Unity editor

// NB: Haven't properly tested that 2nd one because if you're using the editor you're probably in dev mode, so the backend (VoxignPipeline.py) is already
// configured to skip Unity providing input and just capture the audio in python directly

// This file will primarily feature tooltips instead of raw comments for convenience when configuring in the Unity editor

[RequireComponent(typeof(SequenceReceiver))]
public class VoiceAudioStreamer : MonoBehaviour
{
    [Header("Audio")]
    [Tooltip("Requested capture rate. The backend is told the actual rate reported by the device.")]
    [SerializeField] private int sampleRate = 16000;
    [Range(20, 500)]
    [SerializeField] private int chunkMilliseconds = 100;

    [Header("Development")]
    [Tooltip("DEV_MODE: When enabled, the backend records its own microphone, so nothing is streamed from Unity. Turn this off to stream this editor's microphone.")]
    [SerializeField] private bool devMode = true;
    [Tooltip("Ask for the Android microphone permission before capturing.")]
    [SerializeField] private bool requestPermissionOnStart = true;
    
    [Header("Compression")]
    [Tooltip("Encode audio as 8-bit G.711 mu-law (μ-law), halving the required streaming bandwidth.")]
    [SerializeField] private bool compressAudio = true;

    [Header("Events")]
    public StreamingStateEvent onStreamingChanged = new StreamingStateEvent();

    public bool IsStreaming { get; private set; }
    public bool DevMode { get { return devMode; } }

    private SequenceReceiver receiver;
    private AudioClip clip;
    
    // Coroutine responsible for starting the microphone capture
    private Coroutine starter;
    // Sends the audio to the backend
    private Coroutine pump;
    private string deviceName;
    private bool loggedFirstChunk;

    void Awake()
    {
        receiver = GetComponent<SequenceReceiver>();

        // Handler in case dev mode is enabled on a mobile device
        if (devMode && Application.isMobilePlatform)
        {
            devMode = false;
            Debug.LogWarning("[VoiceAudioStreamer] DEV_MODE is enabled in a mobile build; streaming this device's microphone instead.");
        }
    }

    void OnDisable()
    {
        StopStreaming();
    }

    public void SetStreaming(bool value)
    {
        if (value) StartStreaming();
        else StopStreaming();
    }

    public void ToggleStreaming()
    {
        SetStreaming(!IsStreaming);
    }

    public void StartStreaming()
    {
        if (IsStreaming || starter != null) return;
        
        // Dont attempt to stream if dev mode is on
        if (devMode)
        {
            Debug.Log("[VoiceAudioStreamer] DEV_MODE is on: the backend records its own microphone, so no audio is streamed.");
            return;
        }

        starter = StartCoroutine(StartCapture());
    }

    public void StopStreaming()
    {
        if (starter != null)
        {
            StopCoroutine(starter);
            starter = null;
        }
        if (pump != null)
        {
            StopCoroutine(pump);
            pump = null;
        }
        if (clip != null)
        {
            Microphone.End(deviceName);
            clip = null;
        }

        if (!IsStreaming) return;

        IsStreaming = false;

        // Asks the backend to flush the last utterance and close the Deepgram stream
        receiver.SendControl("{\"type\":\"audio_end\"}");
        onStreamingChanged.Invoke(false);
        Debug.Log("[VoiceAudioStreamer] Microphone streaming stopped.");
    }

    // Reports a start failure so the UI switch can automatically fall back to the off position
    private void FailStreaming(string message)
    {
        Debug.LogError(message);
        onStreamingChanged.Invoke(false);
    }

    private IEnumerator StartCapture()
    {
        if (Application.platform == RuntimePlatform.Android && requestPermissionOnStart
            && !Permission.HasUserAuthorizedPermission(Permission.Microphone))
        {
            Permission.RequestUserPermission(Permission.Microphone);

            float timeout = 60f;
            while (!Permission.HasUserAuthorizedPermission(Permission.Microphone) && timeout > 0f)
            {
                timeout -= Time.unscaledDeltaTime;
                yield return null;
            }

            if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
            {
                FailStreaming("[VoiceAudioStreamer] Microphone permission denied; audio is not streamed.");
                starter = null;
                yield break;
            }
        }

        if (Microphone.devices == null || Microphone.devices.Length == 0)
        {
            FailStreaming("[VoiceAudioStreamer] No microphone device found.");
            starter = null;
            yield break;
        }

        deviceName = null;
        try
        {
            clip = Microphone.Start(deviceName, true, 1, sampleRate);
        }
        catch (Exception e)
        {
            FailStreaming($"[VoiceAudioStreamer] Could not start the microphone: {e.Message}");
            clip = null;
        }

        if (clip == null)
        {
            starter = null;
            yield break;
        }

        float wait = 2f;
        while (Microphone.GetPosition(deviceName) <= 0 && wait > 0f)
        {
            wait -= Time.unscaledDeltaTime;
            yield return null;
        }

        if (Microphone.GetPosition(deviceName) <= 0)
        {
            Microphone.End(deviceName);
            clip = null;
            FailStreaming("[VoiceAudioStreamer] The microphone did not start.");
            starter = null;
            yield break;
        }

        IsStreaming = true;
        onStreamingChanged.Invoke(true);
        Debug.Log($"[VoiceAudioStreamer] Streaming microphone audio at {clip.frequency} Hz.");
        loggedFirstChunk = false;
        starter = null;
        pump = StartCoroutine(Pump());
    }

    // Reads the microphone in fixed chunks and forwards them as PCM16 while the backend is connected
    private IEnumerator Pump()
    {
        int rate = clip.frequency;
        int chunkSamples = Mathf.Max(1, Mathf.RoundToInt(rate * (chunkMilliseconds / 1000f)));
        int clipSamples = clip.samples;
        float[] samples = new float[chunkSamples];
        byte[] pcm = new byte[chunkSamples * 2];
        byte[] mulaw = new byte[chunkSamples];
        int last = Microphone.GetPosition(deviceName);
        bool configSent = false;

        while (IsStreaming)
        {   
            // Skip whatever was captured while offline instead of replaying stale audio
            if (!receiver.IsConnected)
            {
                last = Microphone.GetPosition(deviceName);
                configSent = false;
                yield return null;
                continue;
            }

            // Send the audio configuration to the backend if it hasn't been notified yet
            if (!configSent)
            {
                string encoding = compressAudio ? "mulaw" : "pcm16";
                receiver.SendControl($"{{\"type\":\"audio_config\",\"sampleRate\":{rate},\"channels\":1,\"encoding\":\"{encoding}\"}}");
                configSent = true;
            }
            
            // Determine the current recording position (time-wise) and how many samples are available for processing
            int position = Microphone.GetPosition(deviceName);
            int available = position >= last ? position - last : clipSamples - last + position;
            if (available < chunkSamples)
            {
                yield return null;
                continue;
            }

            ReadSamples(samples, last, chunkSamples);
            last = (last + chunkSamples) % clipSamples;
            
            // Compress for transmission speed and send the audio chunk to the backend
            bool sent;
            if (compressAudio)
            {
                EncodeMuLaw(samples, mulaw);
                sent = receiver.SendAudio(mulaw);
            }
            else
            {
                EncodePcm16(samples, pcm);
                sent = receiver.SendAudio(pcm);
            }
            if (sent)
            {
                if (!loggedFirstChunk)
                {
                    loggedFirstChunk = true;
                    int bytes = compressAudio ? mulaw.Length : pcm.Length;
                    Debug.Log($"[VoiceAudioStreamer] First audio chunk sent ({bytes} bytes, {(compressAudio ? "mulaw" : "pcm16")}).");
                }
            }
            yield return null;
        }
    }

    // Reads a chunk from the looping microphone clip, making sure to correctly stitch the two halves where it wraps
    private void ReadSamples(float[] destination, int startSample, int count)
    {
        int clipSamples = clip.samples;
        if (startSample + count <= clipSamples)
        {
            clip.GetData(destination, startSample);
            return;
        }

        int head = clipSamples - startSample;
        float[] headBuffer = new float[head];
        clip.GetData(headBuffer, startSample);
        Array.Copy(headBuffer, 0, destination, 0, head);

        int tail = count - head;
        float[] tailBuffer = new float[tail];
        clip.GetData(tailBuffer, 0);
        Array.Copy(tailBuffer, 0, destination, head, tail);
    }

    // Encodes the audio samples as 16-bit PCM
    private static void EncodePcm16(float[] samples, byte[] destination)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            int value = Mathf.Clamp(Mathf.RoundToInt(samples[i] * 32767f), short.MinValue, short.MaxValue);
            destination[i * 2] = (byte)(value & 0xFF);
            destination[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }
    }

    // Compresses the audio samples using Mu-Law
    private static void EncodeMuLaw(float[] samples, byte[] destination)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            int value = Mathf.Clamp(Mathf.RoundToInt(samples[i] * 32767f), short.MinValue, short.MaxValue);
            destination[i] = LinearToMuLaw(value);
        }
    }

    // Actual μ-law math
    private static byte LinearToMuLaw(int sample)
    {
        const int Bias = 0x84;
        const int Clip = 32635;

        int sign = sample < 0 ? 0x80 : 0;
        if (sample < 0) sample = -sample;
        if (sample > Clip) sample = Clip;
        sample += Bias;

        int exponent = 7;
        for (int mask = 0x4000; (sample & mask) == 0 && exponent > 0; mask >>= 1)
            exponent--;

        int mantissa = (sample >> (exponent + 3)) & 0x0F;
        return (byte)~(sign | (exponent << 4) | mantissa);
    }
}
