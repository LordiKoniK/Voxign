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

    // Sign queue from PipelineTester
    private readonly Queue<LandmarkSeries> queue = new Queue<LandmarkSeries>();
    private Coroutine playback;

    public bool IsPlaying => playback != null;

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
        if (series == null || series.frames == null || series.frames.Count == 0) return;

        queue.Enqueue(series);
        if (playback == null) playback = StartCoroutine(PlayQueue());
    }

    // Coroutine that plays signs from the queue one by one
    private IEnumerator PlayQueue()
    {
        while (!controller.IsInitialized) yield return null;

        while (queue.Count > 0)
        {
            LandmarkSeries series = queue.Dequeue();
            mapper.Initialize(series.aspectRatio);

            // Release hands that are not used in this sign (stops them from freezing wherever the last sign left them)
            if (!UsesHand(series, true)) controller.ReleaseHand("left");
            if (!UsesHand(series, false)) controller.ReleaseHand("right");

            // Blend into the sign's opening pose before playing it (prevent teleporting from the previous sign's end)
            controller.ApplyRotations(solver.ApplyConstraints(BuildOpeningPose(series)));
            controller.BeginTransition(transitionDuration);
            yield return new WaitForSeconds(transitionDuration);

            Debug.Log($"[SignPlayer] Playing '{series.glossLabel}' ({series.frames.Count} frames).");
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
