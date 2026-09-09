using System.Collections.Generic;
using UnityEngine;

public class LandmarkMapper : MonoBehaviour
{
    private Dictionary<string, Quaternion> neutralPoseCache;

    public void Initialize(Dictionary<string, Quaternion> neutralPose)
    {
        neutralPoseCache = neutralPose;
        Debug.Log($"[LandmarkMapper] Initialized with {neutralPoseCache.Count} neutral bone poses.");
    }

    public BoneRotationSet MapFrame(LandmarkFrame frame)
    {
        BoneRotationSet boneRotations = new BoneRotationSet();

        // 1. Map Upper Body (Pose Landmarks)
        if (frame.poseLandmarks != null && frame.poseLandmarks.Count >= 17)
        {
            MapUpperBodyPose(frame.poseLandmarks, boneRotations);
        }

        // 2. Map Hands
        if (frame.handLandmarksLeft != null && frame.handLandmarksLeft.Count == 21)
        {
            MapFingers(frame.handLandmarksLeft, boneRotations, "left", 1f);
        }

        if (frame.handLandmarksRight != null && frame.handLandmarksRight.Count == 21)
        {
            MapFingers(frame.handLandmarksRight, boneRotations, "right", -1f);
        }

        return boneRotations;
    }

    private void MapUpperBodyPose(List<Vector3> pose, BoneRotationSet boneSet)
    {
        // MediaPipe Pose Keypoints:
        // 11: Left Shoulder, 12: Right Shoulder
        // 13: Left Elbow,    14: Right Elbow
        // 15: Left Wrist,    16: Right Wrist

        // Calculate left upper arm Rotation
        Vector3 leftArmDirection = (pose[13] - pose[11]).normalized;
        if (leftArmDirection != Vector3.zero)
        {
            Quaternion leftArmRot = Quaternion.FromToRotation(Vector3.left, leftArmDirection);
            boneSet.SetRotation("leftArm", ApplyNeutralOffset("leftArm", leftArmRot));
        }

        // Left forearm
        Vector3 leftForearmDirection = (pose[15] - pose[13]).normalized;
        if (leftForearmDirection != Vector3.zero)
        {
            Quaternion leftForearmRot = Quaternion.FromToRotation(Vector3.left, leftForearmDirection);
            boneSet.SetRotation("leftForeArm", ApplyNeutralOffset("leftForeArm", leftForearmRot));
        }

        // Right upper arm
        Vector3 rightArmDirection = (pose[14] - pose[12]).normalized;
        if (rightArmDirection != Vector3.zero)
        {
            Quaternion rightArmRot = Quaternion.FromToRotation(Vector3.right, rightArmDirection);
            boneSet.SetRotation("rightArm", ApplyNeutralOffset("rightArm", rightArmRot));
        }

        // Right forearm
        Vector3 rightForearmDirection = (pose[16] - pose[14]).normalized;
        if (rightForearmDirection != Vector3.zero)
        {
            Quaternion rightForearmRot = Quaternion.FromToRotation(Vector3.right, rightForearmDirection);
            boneSet.SetRotation("rightForeArm", ApplyNeutralOffset("rightForeArm", rightForearmRot));
        }
    }

    private void MapFingers(List<Vector3> landmarks, BoneRotationSet boneSet, string side, float axisMultiplier)
    {
        // Thumb
        boneSet.SetRotation($"{side}Thumb1", CalculateCurl(landmarks[0], landmarks[1], landmarks[2], $"{side}Thumb1", axisMultiplier));
        boneSet.SetRotation($"{side}Thumb2", CalculateCurl(landmarks[1], landmarks[2], landmarks[3], $"{side}Thumb2", axisMultiplier));
        boneSet.SetRotation($"{side}Thumb3", CalculateCurl(landmarks[2], landmarks[3], landmarks[4], $"{side}Thumb3", axisMultiplier)); 

        // Other 4 fingers
        string[] fingers = { "Index", "Middle", "Ring", "Pinky" };
        int[] baseIndices = { 5, 9, 13, 17 };

        for (int i = 0; i < fingers.Length; i++)
        {
            int b = baseIndices[i];
            string fName = fingers[i];

            boneSet.SetRotation($"{side}{fName}1", CalculateCurl(landmarks[0], landmarks[b], landmarks[b + 1], $"{side}{fName}1", axisMultiplier));
            boneSet.SetRotation($"{side}{fName}2", CalculateCurl(landmarks[b], landmarks[b + 1], landmarks[b + 2], $"{side}{fName}2", axisMultiplier));
            boneSet.SetRotation($"{side}{fName}3", CalculateCurl(landmarks[b + 1], landmarks[b + 2], landmarks[b + 3], $"{side}{fName}3", axisMultiplier));
        }
    }

    private Quaternion CalculateCurl(Vector3 parentJoint, Vector3 currentJoint, Vector3 childJoint, string boneName, float axisMultiplier)
    {
        Vector3 bone1 = (currentJoint - parentJoint).normalized;
        Vector3 bone2 = (childJoint - currentJoint).normalized;
        
        float bendAngle = Vector3.Angle(bone1, bone2);

        Quaternion localCurl = boneName.Contains("Thumb") 
            ? Quaternion.Euler(0, 0, bendAngle * axisMultiplier) 
            : Quaternion.Euler(bendAngle * axisMultiplier, 0, 0);

        return ApplyNeutralOffset(boneName, localCurl);
    }

    private Quaternion ApplyNeutralOffset(string boneName, Quaternion calculatedRot)
    {
        if (neutralPoseCache != null && neutralPoseCache.TryGetValue(boneName, out Quaternion neutral))
        {
            return neutral * calculatedRot;
        }
        return calculatedRot;
    }
}