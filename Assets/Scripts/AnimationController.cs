using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(Animator))]

// Controls the overall animation of the avatar, applying smoothing and transitions
public class AnimationController : MonoBehaviour
{
    // Smoothing speed for bone rotations: higher is more snappy, lower is smoother, 0 disables smoothing entirely
    [SerializeField] private float smoothingSpeed = 20f;

    private Animator animator;

    // Maps bone names to their corresponding transforms
    private readonly Dictionary<string, Transform> boneMap = new Dictionary<string, Transform>();

    // Maps each bone to its immediate child bone (used for hierarchical processing)
    private readonly Dictionary<string, string> childKeys = new Dictionary<string, string>();
    private readonly List<string> applyOrder = new List<string>();

    // Stores the local rotations of bones in the rest pose
    private readonly Dictionary<string, Quaternion> restLocalRotations = new Dictionary<string, Quaternion>();

    // Bone-local axis pointing toward the next joint, measured in the rest pose (used for IK calculations)
    private readonly Dictionary<string, Vector3> boneAxes = new Dictionary<string, Vector3>();

    // Hand-local index -> pinky finger axis, used to determine hand roll
    private readonly Dictionary<string, Vector3> hintAxes = new Dictionary<string, Vector3>();

    // Targets for the current, smoothed, and rest poses of each bone
    private readonly Dictionary<string, BoneTarget> targets = new Dictionary<string, BoneTarget>();
    private readonly Dictionary<string, BoneTarget> smoothedTargets = new Dictionary<string, BoneTarget>();
    private readonly Dictionary<string, BoneTarget> restTargets = new Dictionary<string, BoneTarget>();
    
    // Targets for the previous pose during transitions
    private readonly Dictionary<string, BoneTarget> transitionFrom = new Dictionary<string, BoneTarget>();
    private float transitionStart;
    private float transitionDuration;

    public bool IsInitialized { get; private set; }

    void Start()
    {
        animator = GetComponent<Animator>();
        AutoAssignBones();
        CaptureRestPose();
        IsInitialized = true;

        Debug.Log($"[AnimationController] Initialized. Tracking {boneMap.Count} arm & hand bones.");
    }

    // Updates the bone rotations each frame, applying smoothing and transitions
    // LateUpdate function wrapper required to ensure changes are applied after Unity's internal processing is done
    // Without this, everything is internally overwritten by Unity before you even see the frame
    void LateUpdate()
    {
        if (targets.Count == 0) return;

        float t = smoothingSpeed > 0f ? 1f - Mathf.Exp(-smoothingSpeed * Time.deltaTime) : 1f;
        float transitionBlend = 1f;
        if (transitionDuration > 0f)
        {
            float p = Mathf.Clamp01((Time.time - transitionStart) / transitionDuration);
            transitionBlend = p * p * (3f - 2f * p);
        }

        // Apply bone rotations
        // Resets to the rest pose values each frame before applying the current target rotations
        // This ensures that we always start from a known constant instead of constantly having to recalculate cumulative rotations
        foreach (string key in applyOrder)
        {
            Transform bone = boneMap[key];
            bone.localRotation = restLocalRotations[key];

            if (!targets.TryGetValue(key, out BoneTarget target)) continue;

            if (transitionBlend < 1f && transitionFrom.TryGetValue(key, out BoneTarget from))
            {
                target = Blend(from, target, transitionBlend);
            }

            BoneTarget current = smoothedTargets.TryGetValue(key, out BoneTarget previous)
                ? Blend(previous, target, t)
                : target;
            smoothedTargets[key] = current;

            AimBone(key, bone, current);
        }
    }

    public void ApplyRotations(BoneRotationSet incoming)
    {
        if (incoming == null || incoming.targets == null) return;

        foreach (var pair in incoming.targets)
        {
            targets[pair.Key] = pair.Value;
        }
    }

    // Eases from the currently displayed pose to the target pose over the given duration
    public void BeginTransition(float duration)
    {
        transitionFrom.Clear();
        foreach (var pair in smoothedTargets) transitionFrom[pair.Key] = pair.Value;
        transitionStart = Time.time;
        transitionDuration = duration;
    }

    // Sends an arm and its child bones back to rest pose (e.g. for a sign that doesn't use it)
    public void ReleaseHand(string side)
    {
        foreach (string key in applyOrder)
        {
            if (key.StartsWith(side) && !key.Contains("Arm")) targets[key] = restTargets[key];
        }
    }

    // Blends (smoothing) between two bone positions during movement
    private static BoneTarget Blend(BoneTarget from, BoneTarget to, float t)
    {
        return new BoneTarget
        {
            direction = Vector3.Slerp(from.direction, to.direction, t),
            hint = from.HasHint && to.HasHint ? Vector3.Slerp(from.hint, to.hint, t) : to.hint
        };
    }

    // Rotates a single bone towards its target direction, optionally using a hint for orientation (e.g. hand roll with index-pinky relationship)
    private void AimBone(string key, Transform bone, BoneTarget target)
    {
        Vector3 worldDir = transform.rotation * target.direction;
        Vector3 axis = boneAxes[key];

        if (target.HasHint && hintAxes.TryGetValue(key, out Vector3 hintAxis))
        {
            Vector3 worldHint = transform.rotation * target.hint;
            Quaternion targetBasis = Quaternion.LookRotation(worldDir, Vector3.Cross(worldDir, worldHint));
            Quaternion restBasis = Quaternion.LookRotation(axis, Vector3.Cross(axis, hintAxis));
            bone.rotation = targetBasis * Quaternion.Inverse(restBasis);
        }
        else
        {
            Vector3 currentDir = bone.rotation * axis;
            bone.rotation = Quaternion.FromToRotation(currentDir, worldDir) * bone.rotation;
        }
    }

    // Automatically map the avatar's bone hierarchy to the system bone keys
    void AutoAssignBones()
    {
        // Parent bones must be added before their children
        foreach (string side in new[] { "Left", "Right" })
        {
            string s = side.ToLower();
            AddBone($"{s}Arm", $"{side}UpperArm", $"{s}ForeArm");
            AddBone($"{s}ForeArm", $"{side}LowerArm", $"{s}Hand");
            AddBone($"{s}Hand", $"{side}Hand", $"{s}Middle1");

            foreach (var (key, unityName) in new[] { ("Thumb", "Thumb"), ("Index", "Index"), ("Middle", "Middle"), ("Ring", "Ring"), ("Pinky", "Little") })
            {
                AddBone($"{s}{key}1", $"{side}{unityName}Proximal", $"{s}{key}2");
                AddBone($"{s}{key}2", $"{side}{unityName}Intermediate", $"{s}{key}3");
                AddBone($"{s}{key}3", $"{side}{unityName}Distal", null);
            }
        }
    }

    // Helper function for bone mapping
    void AddBone(string key, string humanBoneName, string childKey)
    {
        HumanBodyBones humanBone = (HumanBodyBones)System.Enum.Parse(typeof(HumanBodyBones), humanBoneName);
        Transform bone = animator.GetBoneTransform(humanBone);
        if (bone == null) return;

        boneMap[key] = bone;
        childKeys[key] = childKey;
        applyOrder.Add(key);
    }

    // Captures the rest pose of the avatar, storing starting rotations and bone axes for later reference during calculations
    void CaptureRestPose()
    {
        foreach (string key in applyOrder)
        {
            Transform bone = boneMap[key];
            restLocalRotations[key] = bone.localRotation;

            Transform child = null;
            if (childKeys[key] != null) boneMap.TryGetValue(childKeys[key], out child);
            if (child == null && bone.childCount > 0) child = bone.GetChild(0);

            Vector3 worldDir = child != null ? child.position - bone.position : Vector3.zero;
            // Distal bones without an end bone target automatically inherit their parent's direction
            if (worldDir.sqrMagnitude < 1e-8f && bone.parent != null) worldDir = bone.position - bone.parent.position;

            boneAxes[key] = (Quaternion.Inverse(bone.rotation) * worldDir).normalized;
        }

        foreach (string s in new[] { "left", "right" })
        {
            if (boneMap.TryGetValue($"{s}Hand", out Transform hand)
                && boneMap.TryGetValue($"{s}Index1", out Transform index)
                && boneMap.TryGetValue($"{s}Pinky1", out Transform pinky))
            {
                hintAxes[$"{s}Hand"] = (Quaternion.Inverse(hand.rotation) * (index.position - pinky.position)).normalized;
            }
        }

        // Referencable targets that reproduce the rest pose (also the starting point for the very first blend of any movement)
        Quaternion toRoot = Quaternion.Inverse(transform.rotation);
        foreach (string key in applyOrder)
        {
            Quaternion boneRotation = boneMap[key].rotation;
            BoneTarget rest = new BoneTarget { direction = toRoot * (boneRotation * boneAxes[key]) };
            if (hintAxes.TryGetValue(key, out Vector3 hintAxis)) rest.hint = toRoot * (boneRotation * hintAxis);

            restTargets[key] = rest;
            smoothedTargets[key] = rest;
        }
    }
}