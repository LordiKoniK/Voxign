using UnityEngine;
using UnityEngine.Events;
using System.Collections;

// Script for handling the voice detection toggle UI component

[RequireComponent(typeof(UnityEngine.UI.Button))]
public class VoiceToggleScript : MonoBehaviour
{
    [Header("Visuals")]
    public UnityEngine.UI.Image backgroundImage;
    public UnityEngine.UI.Image knobImage;
    public RectTransform knobRect;

    [Header("Colors")]
    public Color backgroundColorOff = new Color(0.20f, 0.20f, 0.20f, 1f);
    public Color backgroundColorOn = new Color(0.42f, 0.76f, 0.28f, 1f);
    public Color knobColorOff = new Color(0.93f, 0.93f, 0.93f, 1f);
    public Color knobColorOn = new Color(0.10f, 0.10f, 0.10f, 1f);

    // On/off animation
    [Header("Animation")]
    public float tweenDuration = 0.22f;
    public AnimationCurve tweenCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
    public float knobPadding = 16f;

    // On/off logic state
    [Header("State")]
    public bool isOn = false;
    public UnityEvent<bool> onValueChanged = new UnityEvent<bool>();

    public bool IsOn { get { return isOn; } }

    private UnityEngine.UI.Button button;
    private RectTransform backgroundRect;
    private RectTransform slideRect;
    private Coroutine tweenCoroutine;
    private bool isInitialized;

    // Lifecycle functions
    void Awake()
    {
        button = GetComponent<UnityEngine.UI.Button>();
        if (button != null)
        {
            button.transition = UnityEngine.UI.Selectable.Transition.None;
            button.onClick.RemoveListener(OnClick);
            button.onClick.AddListener(OnClick);
        }
        ResolveReferences();
    }

    void OnDestroy()
    {
        if (button != null)
            button.onClick.RemoveListener(OnClick);
    }

    void Start()
    {
        ApplyInstant(isOn);
        isInitialized = true;
    }

    void OnEnable()
    {
        if (isInitialized)
            ApplyInstant(isOn);
    }

    void OnDisable()
    {
        if (tweenCoroutine != null)
        {
            StopCoroutine(tweenCoroutine);
            tweenCoroutine = null;
        }
    }

    // Assign the assets if they are not already set
    void ResolveReferences()
    {
        if (backgroundImage == null)
        {
            Transform backdrop = transform.Find("Backdrop");
            if (backdrop != null)
                backgroundImage = backdrop.GetComponent<UnityEngine.UI.Image>();
        }
        if (knobRect == null)
        {
            Transform handle = transform.Find("Handle Slide Area/Handle");
            if (handle != null)
                knobRect = handle as RectTransform;
        }
        if (knobImage == null && knobRect != null)
            knobImage = knobRect.GetComponent<UnityEngine.UI.Image>();

        if (backgroundImage != null)
            backgroundRect = backgroundImage.GetComponent<RectTransform>();
        if (backgroundRect == null)
            backgroundRect = GetComponent<RectTransform>();
        if (knobRect != null)
            slideRect = knobRect.parent as RectTransform;
    }
    
    // Click event handling for the actual toggle 
    public void OnClick()
    {
        SetValue(!isOn);
    }

    public void SetValue(bool value)
    {
        if (isOn == value)
        {
            ApplyInstant(value);
            return;
        }

        isOn = value;

        if (isActiveAndEnabled)
        {
            if (tweenCoroutine != null)
                StopCoroutine(tweenCoroutine);
            tweenCoroutine = StartCoroutine(TweenTo(value));
        }
        else
        {
            ApplyInstant(value);
        }

        onValueChanged.Invoke(isOn);
    }

    public void SetValueWithoutNotify(bool value)
    {
        isOn = value;
        ApplyInstant(value);
    }

    // Transition animation 
    IEnumerator TweenTo(bool targetOn)
    {
        Color bgFrom = backgroundImage != null ? backgroundImage.color : backgroundColorOff;
        Color knobFrom = knobImage != null ? knobImage.color : knobColorOff;
        float knobFromX = knobRect != null ? knobRect.anchoredPosition.x : KnobStartX;
        Color bgTo = targetOn ? backgroundColorOn : backgroundColorOff;
        Color knobTo = targetOn ? knobColorOn : knobColorOff;
        float knobToX = targetOn ? KnobEndX : KnobStartX;

        float elapsed = 0f;
        while (elapsed < tweenDuration)
        {
            elapsed += Time.unscaledDeltaTime;
            float k = tweenCurve.Evaluate(Mathf.Clamp01(elapsed / tweenDuration));
            ApplyVisuals(Color.Lerp(bgFrom, bgTo, k), Color.Lerp(knobFrom, knobTo, k), Mathf.Lerp(knobFromX, knobToX, k));
            yield return null;
        }

        ApplyInstant(targetOn);   
        tweenCoroutine = null;
    }

    // Option to apply the toggle instantly without playing the animation (in case animation transition time is set to 0)
    void ApplyInstant(bool targetOn)
    {
        ApplyVisuals(
            targetOn ? backgroundColorOn : backgroundColorOff,
            targetOn ? knobColorOn : knobColorOff,
            targetOn ? KnobEndX : KnobStartX);
    }
    
    // Apply visual state of the toggle (background color, knob color, knob position)
    void ApplyVisuals(Color backgroundColor, Color knobColor, float knobX)
    {
        if (backgroundImage != null)
            backgroundImage.color = backgroundColor;

        if (knobImage != null)
            knobImage.color = knobColor;

        if (knobRect != null)
        {
            Vector2 pos = knobRect.anchoredPosition;
            pos.x = knobX;
            pos.y = 0f;
            knobRect.anchoredPosition = pos;
        }
    }

    // Toggle's visual properties (dimensions/position)
    float SlideWidth
    {
        get
        {
            if (slideRect != null && slideRect.rect.width > 1f)
                return slideRect.rect.width;
            if (backgroundRect != null)
                return backgroundRect.rect.width;
            return 0f;
        }
    }

    float KnobWidth
    {
        get
        {
            if (knobRect != null && knobRect.rect.width > 1f)
                return knobRect.rect.width;
            if (backgroundRect != null)
                return Mathf.Max(0f, backgroundRect.rect.height - knobPadding * 2f);
            return 0f;
        }
    }

    float KnobStartX { get { return knobPadding; } }

    float KnobEndX { get { return Mathf.Max(knobPadding, SlideWidth - knobPadding - KnobWidth); } }
}
