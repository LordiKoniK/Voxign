using System.Collections.Generic;
using UnityEngine;

// This is the missing definition!
[System.Serializable]
public class RotationConstraint
{
    public float minZ;
    public float maxZ;

    public RotationConstraint(float min, float max)
    {
        minZ = min;
        maxZ = max;
    }
}

public class IKSolver : MonoBehaviour
{
    private Dictionary<string, RotationConstraint> jointConstraints = new Dictionary<string, RotationConstraint>();

    public void Initialize()
    {
        string[] leftFingers = { "leftIndex", "leftMiddle", "leftRing", "leftPinky" };
        string[] rightFingers = { "rightIndex", "rightMiddle", "rightRing", "rightPinky" };

        // Rotation constraints for each finger 
        foreach (string finger in leftFingers)
        {
            jointConstraints[$"{finger}1"] = new RotationConstraint(-90f, 0f);
            jointConstraints[$"{finger}2"] = new RotationConstraint(-100f, 0f);
            jointConstraints[$"{finger}3"] = new RotationConstraint(-80f, 0f);
        }
        jointConstraints["leftThumb1"] = new RotationConstraint(-60f, 0f);
        jointConstraints["leftThumb2"] = new RotationConstraint(-60f, 0f);
        jointConstraints["leftThumb3"] = new RotationConstraint(-90f, 0f);

        foreach (string finger in rightFingers)
        {
            jointConstraints[$"{finger}1"] = new RotationConstraint(0f, 90f);
            jointConstraints[$"{finger}2"] = new RotationConstraint(0f, 100f);
            jointConstraints[$"{finger}3"] = new RotationConstraint(0f, 80f);
        }
        jointConstraints["rightThumb1"] = new RotationConstraint(0f, 60f);
        jointConstraints["rightThumb2"] = new RotationConstraint(0f, 60f);
        jointConstraints["rightThumb3"] = new RotationConstraint(0f, 90f);

        jointConstraints["leftArm"]      = new RotationConstraint(-180f, 180f);
        jointConstraints["leftForeArm"]  = new RotationConstraint(-180f, 180f);
        jointConstraints["rightArm"]     = new RotationConstraint(-180f, 180f);
        jointConstraints["rightForeArm"] = new RotationConstraint(-180f, 180f);
    }

    public BoneRotationSet ApplyConstraints(BoneRotationSet rotations)
    {
        BoneRotationSet constrainedSet = new BoneRotationSet();
        
        foreach (var pair in rotations.rotations)
        {
            constrainedSet.SetRotation(pair.Key, ClampRotation(pair.Key, pair.Value));
        }
        
        return constrainedSet;
    }

    private Quaternion ClampRotation(string boneName, Quaternion rotation)
    {
        if (!jointConstraints.ContainsKey(boneName)) return rotation;

        RotationConstraint constraint = jointConstraints[boneName];
        Vector3 euler = rotation.eulerAngles;

        // Thumbs and Arms still use Z
        if (boneName.Contains("Thumb") || boneName.Contains("Arm"))
        {
            float z = euler.z;
            if (z > 180f) z -= 360f;
            float clampedZ = Mathf.Clamp(z, constraint.minZ, constraint.maxZ);
            return Quaternion.Euler(euler.x, euler.y, clampedZ);
        }
        // Main fingers use X
        else
        {
            float x = euler.x;
            if (x > 180f) x -= 360f;
            
            // Wide clamp to prevent conflict with axisMultiplier in the Mapper
            float clampedX = Mathf.Clamp(x, -100f, 100f); 
            return Quaternion.Euler(clampedX, euler.y, euler.z);
        }
    }
}