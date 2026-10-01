using System.Collections;
using System.Collections.Generic;
using UnityEngine;


// Script to test the full pipeline: Landmark mapping, IK solving, and animation application

public class PipelineTester : MonoBehaviour
{
    private LandmarkMapper mapper;
    private IKSolver solver;
    private AnimationController controller;

    [TextArea(5, 20)]
    public string jsonPayload; 

    IEnumerator Start()
    {
        mapper = GetComponent<LandmarkMapper>();
        solver = GetComponent<IKSolver>();
        controller = GetComponent<AnimationController>();

        if (mapper == null || solver == null || controller == null)
        {
            Debug.LogError("[PipelineTester] Missing required pipeline components!");
            yield break;
        }

        yield return new WaitForEndOfFrame();
        mapper.Initialize(controller.GetNeutralPose());
        solver.Initialize();

        if (string.IsNullOrEmpty(jsonPayload))
        {
            Debug.LogWarning("[PipelineTester] No JSON payload provided. Please paste the Python output into the Inspector.");
            yield break;
        }

        // Deserialize Mediapipe JSON payload into a LandmarkSeries
        LandmarkSeries series = JsonUtility.FromJson<LandmarkSeries>(jsonPayload);
        Debug.Log($"[PipelineTester] Loaded '{series.glossLabel}' with {series.frameCount} frames.");

        // Start playback
        StartCoroutine(PlaySignSequence(series));
    }

    IEnumerator PlaySignSequence(LandmarkSeries series)
    {
        float frameInterval = 1f / series.frameRate;

        foreach (LandmarkFrame frame in series.frames)
        {
            BoneRotationSet mappedRotations = mapper.MapFrame(frame);
            BoneRotationSet constrainedRotations = solver.ApplyConstraints(mappedRotations);
            
            // Push the frame to avatar
            controller.ApplyRotations(constrainedRotations);

            // Wait for next frame
            yield return new WaitForSeconds(frameInterval);
        }

        Debug.Log($"[PipelineTester] Finished playing {series.glossLabel}.");
    }
}