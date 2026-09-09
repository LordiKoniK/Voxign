using System.Collections.Generic;
using UnityEngine;

[System.Serializable]
public class LandmarkFrame
{
    public int frameIndex;
    public List<Vector3> handLandmarksLeft = new List<Vector3>();
    public List<Vector3> handLandmarksRight = new List<Vector3>();
    public List<Vector3> poseLandmarks = new List<Vector3>();
}

public class BoneRotationSet
{
    public Dictionary<string, Quaternion> rotations = new Dictionary<string, Quaternion>();
    public Dictionary<string, float> blendshapeWeights = new Dictionary<string, float>();

    public void SetRotation(string boneName, Quaternion rotation)
    {
        rotations[boneName] = rotation;
    }
}