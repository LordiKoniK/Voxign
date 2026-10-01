using System.Collections.Generic;
using UnityEngine;

[System.Serializable]

// LandmarkFrame: A single frame of landmark data, including hand and pose landmarks
public class LandmarkFrame
{
    public int frameIndex;
    public List<Vector3> handLandmarksLeft = new List<Vector3>();
    public List<Vector3> handLandmarksRight = new List<Vector3>();
    public List<Vector3> poseLandmarks = new List<Vector3>();
}

// LandmarkSeries: A set of consecutive landmark frames, representing a motion sequence
public class LandmarkSeries
{
    public string glossLabel;
    public int frameCount;
    public int frameRate = 30;
    public List<LandmarkFrame> frames = new List<LandmarkFrame>();
}

// BoneRotationSet: Stores rotations of bones and blendshape weights
public class BoneRotationSet
{
    public Dictionary<string, Quaternion> rotations = new Dictionary<string, Quaternion>();
    public Dictionary<string, float> blendshapeWeights = new Dictionary<string, float>();

    public void SetRotation(string boneName, Quaternion rotation)
    {
        rotations[boneName] = rotation;
    }
}