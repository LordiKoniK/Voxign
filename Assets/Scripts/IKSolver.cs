using System.Collections.Generic;
using UnityEngine;

// Applies inverse kinematics constraints to the bone rotations
// This ensures that joints do not bend beyond reasonable limits
public class IKSolver : MonoBehaviour
{
    private readonly List<(string bone, string parent, float maxBend)> bendLimits = new List<(string, string, float)>();

    // Define the rotational limits
    public void Initialize()
    {
        bendLimits.Clear();

        foreach (string side in new[] { "left", "right" })
        {   
            // Hierarchical IK limits (-> means dependent on)
            // Forearm -> Arm
            bendLimits.Add(($"{side}ForeArm", $"{side}Arm", 150f));

            // Thumb phalanges [Thumb3 -> Thumb2 -> Thumb1]
            bendLimits.Add(($"{side}Thumb2", $"{side}Thumb1", 70f));
            bendLimits.Add(($"{side}Thumb3", $"{side}Thumb2", 90f));
            
            // Loop for other fingers (same 3 -> 2 -> 1 structure for distal -> intermediate -> proximal phalanges)
            foreach (string finger in new[] { "Index", "Middle", "Ring", "Pinky" })
            {
                bendLimits.Add(($"{side}{finger}1", $"{side}Hand", 90f));
                bendLimits.Add(($"{side}{finger}2", $"{side}{finger}1", 110f));
                bendLimits.Add(($"{side}{finger}3", $"{side}{finger}2", 90f));
            }
        }
    }

    // Applies IK limits to the given set of bone rotations
    public BoneRotationSet ApplyConstraints(BoneRotationSet input)
    {
        BoneRotationSet constrained = new BoneRotationSet();

        // Copy incoming bone rotations/blendshape weights to a set
        foreach (var pair in input.targets) constrained.targets[pair.Key] = pair.Value;
        foreach (var pair in input.blendshapeWeights) constrained.blendshapeWeights[pair.Key] = pair.Value;

        foreach (var (bone, parent, maxBend) in bendLimits)
        {   
            // Skip if either the bone or its parent is not present in the set (prevent reference errors)
            if (!constrained.targets.TryGetValue(bone, out BoneTarget target)) continue;
            if (!constrained.targets.TryGetValue(parent, out BoneTarget parentTarget)) continue;

            // Skip if the joint's rotation angle is already within the allowed maximum bend
            if (Vector3.Angle(parentTarget.direction, target.direction) <= maxBend) continue;
            
            // Apply the maximum bend constraint to the joint's rotation
            target.direction = Vector3.RotateTowards(parentTarget.direction, target.direction, maxBend * Mathf.Deg2Rad, 0f);
            constrained.targets[bone] = target;
        }

        return constrained;
    }
}