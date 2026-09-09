using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(Animator))]
public class AnimationController : MonoBehaviour
{
    private Animator animator;
    private Dictionary<string, Transform> boneMap = new Dictionary<string, Transform>();
    
    private Dictionary<string, Quaternion> neutralPose = new Dictionary<string, Quaternion>();
    private Dictionary<string, Quaternion> targetPoses = new Dictionary<string, Quaternion>();

    void Start()
    {
        animator = GetComponent<Animator>();
        AutoAssignBones();
        CaptureNeutralPose();
        
        Debug.Log("[AnimationController] Start initialized successfully. Upper body & fingers tracked.");
    }

    void LateUpdate()
    {
        foreach (var pair in targetPoses)
        {
            if (boneMap.TryGetValue(pair.Key, out Transform boneTransform) && boneTransform != null)
            {
                boneTransform.localRotation = pair.Value;
            }
        }
    }

    public Dictionary<string, Quaternion> GetNeutralPose()
    {
        return neutralPose;
    }

    public void ApplyRotations(BoneRotationSet incomingRotations)
    {
        if (incomingRotations == null || incomingRotations.rotations == null) return;

        foreach (var pair in incomingRotations.rotations)
        {
            targetPoses[pair.Key] = pair.Value;
        }
    }

    void AutoAssignBones()
    {
        // Torso & Head
        boneMap["spine"]        = animator.GetBoneTransform(HumanBodyBones.Spine);
        boneMap["spine1"]       = animator.GetBoneTransform(HumanBodyBones.Chest);
        boneMap["spine2"]       = animator.GetBoneTransform(HumanBodyBones.UpperChest);
        boneMap["neck"]         = animator.GetBoneTransform(HumanBodyBones.Neck);
        boneMap["head"]         = animator.GetBoneTransform(HumanBodyBones.Head);

        // Arms & Shoulders
        boneMap["leftShoulder"] = animator.GetBoneTransform(HumanBodyBones.LeftShoulder);
        boneMap["rightShoulder"]= animator.GetBoneTransform(HumanBodyBones.RightShoulder);
        boneMap["leftArm"]      = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        boneMap["rightArm"]     = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
        boneMap["leftForeArm"]  = animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
        boneMap["rightForeArm"] = animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
        boneMap["leftHand"]     = animator.GetBoneTransform(HumanBodyBones.LeftHand);
        boneMap["rightHand"]    = animator.GetBoneTransform(HumanBodyBones.RightHand);

        // Left Hand Fingers
        boneMap["leftThumb1"]  = animator.GetBoneTransform(HumanBodyBones.LeftThumbProximal);
        boneMap["leftThumb2"]  = animator.GetBoneTransform(HumanBodyBones.LeftThumbIntermediate);
        boneMap["leftThumb3"]  = animator.GetBoneTransform(HumanBodyBones.LeftThumbDistal);
        boneMap["leftIndex1"]  = animator.GetBoneTransform(HumanBodyBones.LeftIndexProximal);
        boneMap["leftIndex2"]  = animator.GetBoneTransform(HumanBodyBones.LeftIndexIntermediate);
        boneMap["leftIndex3"]  = animator.GetBoneTransform(HumanBodyBones.LeftIndexDistal);
        boneMap["leftMiddle1"] = animator.GetBoneTransform(HumanBodyBones.LeftMiddleProximal);
        boneMap["leftMiddle2"] = animator.GetBoneTransform(HumanBodyBones.LeftMiddleIntermediate);
        boneMap["leftMiddle3"] = animator.GetBoneTransform(HumanBodyBones.LeftMiddleDistal);
        boneMap["leftRing1"]   = animator.GetBoneTransform(HumanBodyBones.LeftRingProximal);
        boneMap["leftRing2"]   = animator.GetBoneTransform(HumanBodyBones.LeftRingIntermediate);
        boneMap["leftRing3"]   = animator.GetBoneTransform(HumanBodyBones.LeftRingDistal);
        boneMap["leftPinky1"]  = animator.GetBoneTransform(HumanBodyBones.LeftLittleProximal);
        boneMap["leftPinky2"]  = animator.GetBoneTransform(HumanBodyBones.LeftLittleIntermediate);
        boneMap["leftPinky3"]  = animator.GetBoneTransform(HumanBodyBones.LeftLittleDistal);

        // Right Hand Fingers
        boneMap["rightThumb1"]  = animator.GetBoneTransform(HumanBodyBones.RightThumbProximal);
        boneMap["rightThumb2"]  = animator.GetBoneTransform(HumanBodyBones.RightThumbIntermediate);
        boneMap["rightThumb3"]  = animator.GetBoneTransform(HumanBodyBones.RightThumbDistal);
        boneMap["rightIndex1"]  = animator.GetBoneTransform(HumanBodyBones.RightIndexProximal);
        boneMap["rightIndex2"]  = animator.GetBoneTransform(HumanBodyBones.RightIndexIntermediate);
        boneMap["rightIndex3"]  = animator.GetBoneTransform(HumanBodyBones.RightIndexDistal);
        boneMap["rightMiddle1"] = animator.GetBoneTransform(HumanBodyBones.RightMiddleProximal);
        boneMap["rightMiddle2"] = animator.GetBoneTransform(HumanBodyBones.RightMiddleIntermediate);
        boneMap["rightMiddle3"] = animator.GetBoneTransform(HumanBodyBones.RightMiddleDistal);
        boneMap["rightRing1"]   = animator.GetBoneTransform(HumanBodyBones.RightRingProximal);
        boneMap["rightRing2"]   = animator.GetBoneTransform(HumanBodyBones.RightRingIntermediate);
        boneMap["rightRing3"]   = animator.GetBoneTransform(HumanBodyBones.RightRingDistal);
        boneMap["rightPinky1"]  = animator.GetBoneTransform(HumanBodyBones.RightLittleProximal);
        boneMap["rightPinky2"]  = animator.GetBoneTransform(HumanBodyBones.RightLittleIntermediate);
        boneMap["rightPinky3"]  = animator.GetBoneTransform(HumanBodyBones.RightLittleDistal);

        Debug.Log($"[AnimationController] Bones auto-assigned. Total upper body & hand bones tracked: {boneMap.Count}");
    }

    void CaptureNeutralPose()
    {
        foreach (var pair in boneMap)
        {
            if (pair.Value != null)
            {
                neutralPose[pair.Key] = pair.Value.localRotation;
                targetPoses[pair.Key] = pair.Value.localRotation;
            }
        }
    }
}