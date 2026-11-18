using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Plays queued signs back-to-back, blending/interpolating smoothly from the end of each sign into the start of the next
[RequireComponent(typeof(LandmarkMapper), typeof(IKSolver), typeof(AnimationController))]
public class SignPlayer : MonoBehaviour
{
    // No. of seconds dedicated to blending from the end of one sign into the first pose of the next
    [SerializeField] private float transitionDuration = 0.25f;

    private LandmarkMapper mapper;
    private IKSolver solver;
    private AnimationController controller;

    // One queued sign plus the transcript of the utterance it belongs to (only set on the first sign)
    private struct QueuedSign
    {
        public LandmarkSeries series;
        public string transcript;
    }

    // Sign queue from SequenceReceiver or PipelineTester
    private readonly Queue<QueuedSign> queue = new Queue<QueuedSign>();
    private Coroutine playback;

    public bool IsPlaying => playback != null;

    public event Action<string> SignStarted;

    // Raised when the first sign of an utterance starts, so text displays can follow playback
    public event Action<string> UtteranceStarted;

    // Unity lifecycle initialization method
    void Awake()
    {
        mapper = GetComponent<LandmarkMapper>();
        solver = GetComponent<IKSolver>();
        controller = GetComponent<AnimationController>();
        solver.Initialize();
    }

    // Queue up a new sign for playback
    public void Enqueue(LandmarkSeries series)
    {
        Enqueue(series, null);
    }

    // Queue a sign and, on the first sign of an utterance, the transcript that produced it
    public void Enqueue(LandmarkSeries series, string transcript)
    {
        if (series == null || series.frames == null || series.frames.Count == 0) return;

        queue.Enqueue(new QueuedSign { series = series, transcript = transcript });
        if (playback == null) playback = StartCoroutine(PlayQueue());
    }

    // Coroutine that plays signs from the queue one by one
    private IEnumerator PlayQueue()
    {
        while (!controller.IsInitialized) yield return null;

        while (queue.Count > 0)
        {
            QueuedSign next = queue.Dequeue();
            LandmarkSeries series = next.series;
            mapper.Initialize(series.aspectRatio);

            // Release hands that are not used in this sign (stops them from freezing wherever the last sign left them)
            if (!UsesHand(series, true)) controller.ReleaseHand("left");
            if (!UsesHand(series, false)) controller.ReleaseHand("right");

            // Blend into the sign's opening pose before playing it (prevent teleporting from the previous sign's end)
            controller.ApplyRotations(solver.ApplyConstraints(BuildOpeningPose(series)));
            controller.BeginTransition(transitionDuration);
            yield return new WaitForSeconds(transitionDuration);

            Debug.Log($"[SignPlayer] Playing '{series.glossLabel}' ({series.frames.Count} frames).");

            // Announce the utterance on its first sign so the transcript changes with the avatar, not with the network
            if (!string.IsNullOrEmpty(next.transcript)) UtteranceStarted?.Invoke(next.transcript);
            SignStarted?.Invoke(series.glossLabel);
            yield return PlayFrames(series);
        }

        playback = null;
    }

    // Play the signing frames at the set frame rate
    private IEnumerator PlayFrames(LandmarkSeries series)
    {
        float frameRate = series.frameRate > 0f ? series.frameRate : 30f;
        float startTime = Time.time;
        int lastFrame = -1;

        while (true)
        {
            int frameIndex = Mathf.FloorToInt((Time.time - startTime) * frameRate);
            if (frameIndex >= series.frames.Count) break;

            if (frameIndex != lastFrame)    // On each new frame
            {
                // Apply the frame's bone rotations to the avatar
                controller.ApplyRotations(solver.ApplyConstraints(mapper.MapFrame(series.frames[frameIndex])));
                lastFrame = frameIndex;
            }

            yield return null;
        }
    }

    // Create the avatar's opening (starting) pose
    // Done by iterating through all frames for a sign and collecting the first detected rotation for each bone
    private BoneRotationSet BuildOpeningPose(LandmarkSeries series)
    {
        BoneRotationSet opening = new BoneRotationSet();
        foreach (LandmarkFrame frame in series.frames)
        {
            foreach (var pair in mapper.MapFrame(frame).targets)
            {
                opening.targets.TryAdd(pair.Key, pair.Value);
            }
        }
        return opening;
    }

    // Check if a sign uses the specified hand at any point during its frames
    private static bool UsesHand(LandmarkSeries series, bool left)
    {
        foreach (LandmarkFrame frame in series.frames)
        {
            if (LandmarkMapper.IsTracked(left ? frame.handLandmarksLeft : frame.handLandmarksRight)) return true;
        }
        return false;
    }
}
