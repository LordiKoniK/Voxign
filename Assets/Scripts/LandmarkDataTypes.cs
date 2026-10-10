using System.Collections.Generic;
using UnityEngine;

// LandmarkFrame: A single frame of landmark data, containing dedicated lists for each hand and the overall body pose
[System.Serializable]
public class LandmarkFrame
{
    public int frameIndex;
    public List<Vector3> handLandmarksLeft = new List<Vector3>();
    public List<Vector3> handLandmarksRight = new List<Vector3>();
    public List<Vector3> poseLandmarks = new List<Vector3>();
}

// LandmarkSeries: A set of consecutive landmark frames, representing a motion sequence (aka a sign)
[System.Serializable]
public class LandmarkSeries
{
    public string glossLabel;
    public int frameCount;
    public float frameRate = 30f;
    // Source video width/height, needed because MediaPipe normalises x and y separately
    public float aspectRatio = 1f;
    public List<LandmarkFrame> frames = new List<LandmarkFrame>();
}

// LandmarkSequence: Multiple signs (LandmarkSeries objects) to be played consecutively
[System.Serializable]
public class LandmarkSequence
{
    public List<string> glossSequence = new List<string>();
    public List<LandmarkSeries> signs = new List<LandmarkSeries>();
}

// BoneTarget: A bone's direction in avatar root space, with an optional secondary axis to adjust twist if necessary
public struct BoneTarget
{
    public Vector3 direction;
    public Vector3 hint;

    public bool HasHint => hint != Vector3.zero;
}

// BoneRotationSet: Stores multiple bone targets and their associated blendshape weights
public class BoneRotationSet
{
    public Dictionary<string, BoneTarget> targets = new Dictionary<string, BoneTarget>();
    public Dictionary<string, float> blendshapeWeights = new Dictionary<string, float>();

    public void SetDirection(string boneName, Vector3 direction)
    {
        if (direction.sqrMagnitude < 1e-10f) return;
        targets[boneName] = new BoneTarget { direction = direction.normalized };
    }

    public void SetOrientation(string boneName, Vector3 direction, Vector3 hint)
    {
        if (direction.sqrMagnitude < 1e-10f || hint.sqrMagnitude < 1e-10f) return;
        targets[boneName] = new BoneTarget { direction = direction.normalized, hint = hint.normalized };
    }
}