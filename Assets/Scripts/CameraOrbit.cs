using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

// Giving the user proper camera control around the avatar

[RequireComponent(typeof(Camera))]
public class CameraOrbit : MonoBehaviour
{
    [Header("Target")]
    [SerializeField] private Transform target;
    [SerializeField] private Vector3 targetOffset = new Vector3(0f, -0.35f, 0f);

    [Header("Orbit")]
    [SerializeField] private float yaw = 180f;
    [SerializeField] private float pitch = 8f;
    [SerializeField] private float distance = 3.4f;
    [SerializeField] private float minDistance = 1.5f;
    [SerializeField] private float maxDistance = 6f;
    [SerializeField] private float minPitch = -20f;
    [SerializeField] private float maxPitch = 75f;

    [Header("Input")]
    [SerializeField] private float mouseSensitivity = 0.25f;
    [SerializeField] private float touchSensitivity = 0.15f;
    [SerializeField] private float scrollZoomSpeed = 0.4f;

    private bool mouseDragging;
    private float lastPinchDistance;
    private bool pinching;

    void Start()
    {
        if (target == null)
            ResolveTarget();

        ApplyTransform();
    }

    void LateUpdate()
    {
        if (target == null)
        {
            ResolveTarget();
            if (target == null) return;
        }

        HandleMouse();
        HandleTouch();

        yaw = Mathf.Repeat(yaw, 360f);
        pitch = Mathf.Clamp(pitch, minPitch, maxPitch);
        distance = Mathf.Clamp(distance, minDistance, maxDistance);

        ApplyTransform();
    }

    void ResolveTarget()
    {
        SignPlayer player = FindAnyObjectByType<SignPlayer>();
        Transform root = player != null ? player.transform : null;

        // Hardcoded to Louise for now, might consider abstracting if I decide to support multiple avatars
        if (root == null)
        {
            GameObject louise = GameObject.Find("Louise");
            if (louise != null) root = louise.transform;
        }

        if (root == null) return;

        foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
        {
            if (child.name.ToLowerInvariant().Contains("spine1"))
            {
                target = child;
                return;
            }
        }

        target = root;
    }

    // Mouse control: LMB drag to move, scroll to zoom
    void HandleMouse()
    {
        Mouse mouse = Mouse.current;
        if (mouse == null) return;

        if (mouse.leftButton.wasPressedThisFrame)
        {
            mouseDragging = EventSystem.current == null || !EventSystem.current.IsPointerOverGameObject();
        }
        if (!mouse.leftButton.isPressed)
        {
            mouseDragging = false;
        }

        if (mouseDragging)
        {
            Vector2 delta = mouse.delta.ReadValue();
            yaw += delta.x * mouseSensitivity;
            pitch -= delta.y * mouseSensitivity;
        }

        float scroll = mouse.scroll.ReadValue().y;
        if (Mathf.Abs(scroll) > 0.01f)
        {
            distance -= scroll / 120f * scrollZoomSpeed;
        }
    }
    
    // Touch control: one finger drag to move, two finger pinch to zoom
    void HandleTouch()
    {
        Touchscreen touchscreen = Touchscreen.current;
        if (touchscreen == null) return;

        var first = touchscreen.touches[0];
        var second = touchscreen.touches[1];
        bool twoFingers = first.press.isPressed && second.press.isPressed;

        if (twoFingers)
        {
            float pinch = Vector2.Distance(first.position.ReadValue(), second.position.ReadValue());
            if (lastPinchDistance > 0f && pinch > 0f)
                distance *= lastPinchDistance / pinch;

            lastPinchDistance = pinch;
            pinching = true;
            return;
        }

        lastPinchDistance = 0f;
        if (pinching)
        {
            pinching = false;
            return;
        }

        if (first.press.isPressed)
        {
            Vector2 delta = first.delta.ReadValue();
            yaw += delta.x * touchSensitivity;
            pitch -= delta.y * touchSensitivity;
        }
    }

    // Apply the calculated camera transform
    void ApplyTransform()
    {
        if (target == null) return;

        Vector3 focus = target.position + targetOffset;
        Quaternion rotation = Quaternion.Euler(pitch, yaw, 0f);

        transform.SetPositionAndRotation(focus - rotation * Vector3.forward * distance, rotation);
    }
}
