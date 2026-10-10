using System.Collections.Generic;
using UnityEngine;

// Maps MediaPipe landmark coordinates to avatar bone rotations
public class LandmarkMapper : MonoBehaviour
{
    // Finger types and their corresponding MediaPipe landmark indices
    private static readonly string[] Fingers = { "Index", "Middle", "Ring", "Pinky" };
    private static readonly int[] FingerBases = { 5, 9, 13, 17 };

    private float aspectRatio = 1f;

    public void Initialize(float imageAspectRatio)
    {
        aspectRatio = imageAspectRatio > 0f ? imageAspectRatio : 1f;
    }

    // Convert MediaPipe coordinates to Unity coordinate space
    // MediaPipe format: x = left, y = down, z = away from camera 
    // Unity format: x = right, y = up, z = forward
    private Vector3 ToAvatarSpace(Vector3 mp)
    {
        // Apply the coordinate flip
        return new Vector3(-mp.x * aspectRatio, -mp.y, -mp.z * aspectRatio);
    }

    // Maps a single frame of landmarks to the avatar's bone rotations
    public BoneRotationSet MapFrame(LandmarkFrame frame)
    {
        BoneRotationSet boneSet = new BoneRotationSet();
        
        // Map the pose (shoulders and arms) first
        if (frame.poseLandmarks != null && frame.poseLandmarks.Count >= 17)
        {
            MapArm(frame.poseLandmarks, boneSet, "left", 11, 13, 15);
            MapArm(frame.poseLandmarks, boneSet, "right", 12, 14, 16);
        }

        // Only compute hand mappings if they're actually used in this frame/sign
        if (IsTracked(frame.handLandmarksLeft))
        {
            MapHand(frame.handLandmarksLeft, boneSet, "left");
        }

        if (IsTracked(frame.handLandmarksRight))
        {
            MapHand(frame.handLandmarksRight, boneSet, "right");
        }

        return boneSet;
    }

    // Check if a hand is tracked (i.e. the hand has at least one non-zero landmark)
    public static bool IsTracked(List<Vector3> hand)
    {
        if (hand == null || hand.Count < 21) return false;
        foreach (Vector3 lm in hand)
        {
            if (lm != Vector3.zero) return true;
        }
        return false;
    }

    // Actual mapping logic 
    private void MapArm(List<Vector3> pose, BoneRotationSet boneSet, string side, int shoulderIdx, int elbowIdx, int wristIdx)
    {
        if (pose[shoulderIdx] == Vector3.zero || pose[elbowIdx] == Vector3.zero || pose[wristIdx] == Vector3.zero) return;
        
        // Start by applying coordinate flip
        Vector3 shoulder = ToAvatarSpace(pose[shoulderIdx]);
        Vector3 elbow = ToAvatarSpace(pose[elbowIdx]);
        Vector3 wrist = ToAvatarSpace(pose[wristIdx]);

        // Compute direction vectors from the coordinates 
        // This gives us the angle to set the avatar's bone to
        boneSet.SetDirection($"{side}Arm", elbow - shoulder);
        boneSet.SetDirection($"{side}ForeArm", wrist - elbow);
    }

    private void MapHand(List<Vector3> landmarks, BoneRotationSet boneSet, string side)
    {
        Vector3[] lm = new Vector3[21];
        // Apply coordinate flip to all hand landmarks
        for (int i = 0; i < 21; i++) lm[i] = ToAvatarSpace(landmarks[i]);

        // Start by finding the overall hand's position
        // Wrist(0) - middle finger(9) relationship gives us the hand's direction
        // Pointer(5) - pinky(17) relationship determines the roll of the hand (around the wrist-middle finger axis)
        boneSet.SetOrientation($"{side}Hand", lm[9] - lm[0], lm[5] - lm[17]);

        // Actually map the fingers themselves now (each finger has 3 bones)
        // Thumb must be handled individually because it's alignment is inherently different (opposable digit)
        boneSet.SetDirection($"{side}Thumb1", lm[2] - lm[1]);
        boneSet.SetDirection($"{side}Thumb2", lm[3] - lm[2]);
        boneSet.SetDirection($"{side}Thumb3", lm[4] - lm[3]);

        // Map the other 4 fingers
        for (int i = 0; i < Fingers.Length; i++)
        {
            int b = FingerBases[i];
            boneSet.SetDirection($"{side}{Fingers[i]}1", lm[b + 1] - lm[b]);
            boneSet.SetDirection($"{side}{Fingers[i]}2", lm[b + 2] - lm[b + 1]);
            boneSet.SetDirection($"{side}{Fingers[i]}3", lm[b + 3] - lm[b + 2]);
        }
    }
}