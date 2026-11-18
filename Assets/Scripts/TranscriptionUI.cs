using System.Collections;
using TMPro;
using UnityEngine;

// Handles the dynamic display of transcriptions and sign labels in the UI

public class TranscriptionUI : MonoBehaviour
{
    [SerializeField] private TMP_Text transcriptText;
    [SerializeField] private GameObject signPanel;
    [SerializeField] private TMP_Text signText;
    [SerializeField] private string placeholder = "[Recognised input text]";

    private SignPlayer player;
    private bool bound;

    void Start()
    {   
        // Ensure the assets are there
        if (transcriptText != null) transcriptText.text = placeholder;
        if (signPanel != null) signPanel.SetActive(false);

        StartCoroutine(BindWhenAvailable());
    }

    // Binds the UI to the SignPlayer once the avatar scene has loaded
    IEnumerator BindWhenAvailable()
    {
        float deadline = Time.realtimeSinceStartup + 30f;

        while (player == null)
        {
            player = FindAnyObjectByType<SignPlayer>();

            if (player != null) break;
            if (Time.realtimeSinceStartup >= deadline)
            {
                Debug.LogWarning("[TranscriptionUI] No SignPlayer found; transcript display stays inactive.");
                yield break;
            }

            yield return new WaitForSecondsRealtime(0.5f);
        }

        // Both the transcript and the sign label follow playback, so the text matches what is being signed
        player.UtteranceStarted += SetTranscript;
        player.SignStarted += SetSign;
        bound = true;
    }

    // Dynamically update the text fields for the transcript and sign label
    void SetTranscript(string text)
    {
        if (transcriptText != null && !string.IsNullOrEmpty(text))
            transcriptText.text = text;
    }

    void SetSign(string label)
    {
        if (signPanel == null || signText == null) return;

        signText.text = string.IsNullOrEmpty(label) ? string.Empty : label.ToUpperInvariant();
        signPanel.SetActive(true);
    }

    void Update()
    {
        if (bound && player != null && !player.IsPlaying && signPanel != null && signPanel.activeSelf)
            signPanel.SetActive(false);
    }

    void OnDestroy()
    {
        if (player != null)
        {
            player.UtteranceStarted -= SetTranscript;
            player.SignStarted -= SetSign;
        }
    }
}
