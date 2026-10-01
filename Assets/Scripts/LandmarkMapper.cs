using System.Collections.Generic;
using UnityEngine;

public class LandmarkMapper : MonoBehaviour
{
    private Dictionary<string, Quaternion> neutralPoseCache;

    public void Initialize(Dictionary<string, Quaternion> neutralPose)
    {
        neutralPoseCache = neutralPose;
    }

    private Vector3 ToUnitySpace(Vector3 mp)
    {
        // Invert Y and Z to map MediaPipe to Unity world space
        return new Vector3(mp.x, -mp.y, -mp.z);
    }

    public BoneRotationSet MapFrame(LandmarkFrame frame)
    {
        BoneRotationSet boneRotations = new BoneRotationSet();

        if (frame.poseLandmarks != null && frame.poseLandmarks.Count >= 17)
        {
            MapUpperBodyPose(frame.poseLandmarks, boneRotations);
        }

        if (frame.handLandmarksLeft != null && frame.handLandmarksLeft.Count == 21)
        {
            MapFingers(frame.handLandmarksLeft, boneRotations, "left", -1f);
        }

        if (frame.handLandmarksRight != null && frame.handLandmarksRight.Count == 21)
        {
            MapFingers(frame.handLandmarksRight, boneRotations, "right", 1f);
        }

        return boneRotations;
    }

    private void MapUpperBodyPose(List<Vector3> pose, BoneRotationSet boneSet)
    {
        Vector3 leftShoulder = ToUnitySpace(pose[11]);
        Vector3 rightShoulder = ToUnitySpace(pose[12]);
        Vector3 leftElbow = ToUnitySpace(pose[13]);
        Vector3 rightElbow = ToUnitySpace(pose[14]);
        Vector3 leftWrist = ToUnitySpace(pose[15]);
        Vector3 rightWrist = ToUnitySpace(pose[16]);

        // Upper Arms 
        Vector3 leftArmDir = (leftElbow - leftShoulder).normalized;
        if (leftArmDir != Vector3.zero)
        {
            Quaternion leftArmRot = Quaternion.FromToRotation(Vector3.right, leftArmDir);
            boneSet.SetRotation("leftArm", ApplyNeutralOffset("leftArm", leftArmRot));
        }

        Vector3 rightArmDir = (rightElbow - rightShoulder).normalized;
        if (rightArmDir != Vector3.zero)
        {
            Quaternion rightArmRot = Quaternion.FromToRotation(Vector3.left, rightArmDir);
            boneSet.SetRotation("rightArm", ApplyNeutralOffset("rightArm", rightArmRot));
        }

        // Forearms 
        Vector3 leftForearmDir = (leftWrist - leftElbow).normalized;
        float leftElbowBend = Vector3.Angle(leftArmDir, leftForearmDir);
        boneSet.SetRotation("leftForeArm", ApplyNeutralOffset("leftForeArm", Quaternion.Euler(0, 0, -leftElbowBend)));

        Vector3 rightForearmDir = (rightWrist - rightElbow).normalized;
        float rightElbowBend = Vector3.Angle(rightArmDir, rightForearmDir);
        boneSet.SetRotation("rightForeArm", ApplyNeutralOffset("rightForeArm", Quaternion.Euler(0, 0, rightElbowBend)));
    }

    private void MapFingers(List<Vector3> landmarks, BoneRotationSet boneSet, string side, float axisMultiplier)
    {
        List<Vector3> unityLandmarks = new List<Vector3>();
        foreach (var lm in landmarks) unityLandmarks.Add(ToUnitySpace(lm));

        // Thumb
        boneSet.SetRotation($"{side}Thumb1", CalculateCurl(unityLandmarks[0], unityLandmarks[1], unityLandmarks[2], $"{side}Thumb1", axisMultiplier));
        boneSet.SetRotation($"{side}Thumb2", CalculateCurl(unityLandmarks[1], unityLandmarks[2], unityLandmarks[3], $"{side}Thumb2", axisMultiplier));
        boneSet.SetRotation($"{side}Thumb3", CalculateCurl(unityLandmarks[2], unityLandmarks[3], unityLandmarks[4], $"{side}Thumb3", axisMultiplier)); 

        // Main 4 Fingers
        string[] fingers = { "Index", "Middle", "Ring", "Pinky" };
        int[] baseIndices = { 5, 9, 13, 17 };

        for (int i = 0; i < fingers.Length; i++)
        {
            int b = baseIndices[i];
            string fName = fingers[i];

            boneSet.SetRotation($"{side}{fName}1", CalculateCurl(unityLandmarks[0], unityLandmarks[b], unityLandmarks[b + 1], $"{side}{fName}1", axisMultiplier));
            boneSet.SetRotation($"{side}{fName}2", CalculateCurl(unityLandmarks[b], unityLandmarks[b + 1], unityLandmarks[b + 2], $"{side}{fName}2", axisMultiplier));
            boneSet.SetRotation($"{side}{fName}3", CalculateCurl(unityLandmarks[b + 1], unityLandmarks[b + 2], unityLandmarks[b + 3], $"{side}{fName}3", axisMultiplier));
        }
    }

    private Quaternion CalculateCurl(Vector3 parentJoint, Vector3 currentJoint, Vector3 childJoint, string boneName, float axisMultiplier)
    {
        Vector3 bone1 = (currentJoint - parentJoint).normalized;
        Vector3 bone2 = (childJoint - currentJoint).normalized;
        
        float bendAngle = Vector3.Angle(bone1, bone2);
        
        // Use X axis for main 4 fingers and Z axis for thumbs
        Quaternion localCurl = boneName.Contains("Thumb") 
            ? Quaternion.Euler(0, 0, bendAngle * axisMultiplier) 
            : Quaternion.Euler(bendAngle * axisMultiplier, 0, 0);

        return ApplyNeutralOffset(boneName, localCurl);
    }

    private Quaternion ApplyNeutralOffset(string boneName, Quaternion calculatedRot)
    {
        if (neutralPoseCache != null && neutralPoseCache.TryGetValue(boneName, out Quaternion neutral))
        {
            return calculatedRot * neutral;
        }
        return calculatedRot;
    }
}