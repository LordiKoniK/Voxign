using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class PipelineTester : MonoBehaviour
{
    private LandmarkMapper mapper;
    private IKSolver solver;
    private AnimationController controller;

    IEnumerator Start()
    {
        mapper = GetComponent<LandmarkMapper>();
        solver = GetComponent<IKSolver>();
        controller = GetComponent<AnimationController>();

        if (mapper == null || solver == null || controller == null)
        {
            Debug.LogError("[PipelineTester] Missing required pipeline components on this GameObject!");
            yield break;
        }

        yield return new WaitForEndOfFrame();

        // Full-body + hand dummy LandmarkFrame
        LandmarkFrame testFrame = new LandmarkFrame { frameIndex = 100 };

        // Pose landmarks (left Arm raised up and elbow bent forward/up) 
        // Doesn't need to be perfect, these numbers will come from MediaPipe later
        for (int i = 0; i < 33; i++) testFrame.poseLandmarks.Add(Vector3.zero);
        
        testFrame.poseLandmarks[11] = new Vector3(-0.2f, 1.4f, 0f);  // Left Shoulder
        testFrame.poseLandmarks[12] = new Vector3(0.2f, 1.4f, 0f);   // Right Shoulder
        testFrame.poseLandmarks[13] = new Vector3(-0.4f, 1.6f, 0.2f);// Left Elbow (Raised high)
        testFrame.poseLandmarks[14] = new Vector3(0.4f, 1.0f, 0f);   // Right Elbow (At side)
        testFrame.poseLandmarks[15] = new Vector3(-0.2f, 1.8f, 0.4f);// Left Wrist (In front of face/chest)
        testFrame.poseLandmarks[16] = new Vector3(0.4f, 0.8f, 0f);   // Right Wrist

        // Left Hand Landmarks (Simulate curled fingers forming a fist)
        // Wrist at (0,0,0)
        testFrame.handLandmarksLeft.Add(Vector3.zero); // 0: Wrist
        
        // Thumb (Curled inward)
        testFrame.handLandmarksLeft.Add(new Vector3(-0.02f, 0.02f, 0.01f));
        testFrame.handLandmarksLeft.Add(new Vector3(-0.03f, 0.04f, 0.02f));
        testFrame.handLandmarksLeft.Add(new Vector3(-0.02f, 0.04f, 0.04f));
        testFrame.handLandmarksLeft.Add(new Vector3(-0.01f, 0.03f, 0.05f));

        // Index (Curled tightly)
        testFrame.handLandmarksLeft.Add(new Vector3(-0.03f, 0.08f, 0.01f));
        testFrame.handLandmarksLeft.Add(new Vector3(-0.03f, 0.09f, 0.03f));
        testFrame.handLandmarksLeft.Add(new Vector3(-0.03f, 0.07f, 0.04f));
        testFrame.handLandmarksLeft.Add(new Vector3(-0.03f, 0.06f, 0.02f));

        // Middle (Curled tightly)
        testFrame.handLandmarksLeft.Add(new Vector3(-0.01f, 0.08f, 0.01f));
        testFrame.handLandmarksLeft.Add(new Vector3(-0.01f, 0.09f, 0.03f));
        testFrame.handLandmarksLeft.Add(new Vector3(-0.01f, 0.07f, 0.04f));
        testFrame.handLandmarksLeft.Add(new Vector3(-0.01f, 0.06f, 0.02f));

        // Ring (Curled tightly)
        testFrame.handLandmarksLeft.Add(new Vector3(0.01f, 0.08f, 0.01f));
        testFrame.handLandmarksLeft.Add(new Vector3(0.01f, 0.09f, 0.03f));
        testFrame.handLandmarksLeft.Add(new Vector3(0.01f, 0.07f, 0.04f));
        testFrame.handLandmarksLeft.Add(new Vector3(0.01f, 0.06f, 0.02f));

        // Pinky (Curled tightly)
        testFrame.handLandmarksLeft.Add(new Vector3(0.03f, 0.07f, 0.01f));
        testFrame.handLandmarksLeft.Add(new Vector3(0.03f, 0.08f, 0.03f));
        testFrame.handLandmarksLeft.Add(new Vector3(0.03f, 0.06f, 0.04f));
        testFrame.handLandmarksLeft.Add(new Vector3(0.03f, 0.05f, 0.02f));

        // Dummy right hand (resting straight)
        for (int i = 0; i < 21; i++) testFrame.handLandmarksRight.Add(new Vector3(0, i * 0.01f, 0));

        string json = JsonUtility.ToJson(testFrame);
        Debug.Log($"[PipelineTester] Upper-body JSON Payload size: {json.Length} chars.");

        LandmarkFrame receivedFrame = JsonUtility.FromJson<LandmarkFrame>(json);

        // Pass frame through pipeline
        mapper.Initialize(controller.GetNeutralPose());
        solver.Initialize();

        BoneRotationSet mappedRotations = mapper.MapFrame(receivedFrame);
        BoneRotationSet constrainedRotations = solver.ApplyConstraints(mappedRotations);
        controller.ApplyRotations(constrainedRotations);

        Debug.Log("[PipelineTester] Upper body arm raise + fist pose applied successfully.");
    }
}