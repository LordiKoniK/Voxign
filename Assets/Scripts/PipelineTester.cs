using UnityEngine;


// Script to test the full pipeline: load exported JSON and queue its sign frame data on the SignPlayer
// Will be fully integrated into SignPlayer once the entire system is verified and stable

[RequireComponent(typeof(SignPlayer))]
public class PipelineTester : MonoBehaviour
{
    // JSON from LandmarkExtractor goes in here
    public TextAsset jsonFile;

    // Fallback pure-text JSON payload to use if no file is assigned
    [TextArea(5, 20)]
    public string jsonPayload; 

    void Start()
    {
        SignPlayer player = GetComponent<SignPlayer>();

        // Import JSON data
        string json = jsonFile != null ? jsonFile.text : jsonPayload;
        if (string.IsNullOrEmpty(json))
        {
            Debug.LogWarning("[PipelineTester] No JSON provided. Assign a TextAsset or paste the Python output into the Inspector.");
            return;
        }

        // Parse JSON into a LandmarkSequence object
        LandmarkSequence sequence = JsonUtility.FromJson<LandmarkSequence>(json);

        // Handle single-sign exports
        if (sequence == null || sequence.signs == null || sequence.signs.Count == 0)
        {
            LandmarkSeries single = JsonUtility.FromJson<LandmarkSeries>(json);
            sequence = new LandmarkSequence();
            if (single != null && single.frames != null && single.frames.Count > 0) sequence.signs.Add(single);
        }

        // Error handling
        if (sequence.signs.Count == 0)
        {
            Debug.LogError("[PipelineTester] JSON payload contained no frames.");
            return;
        }

        // Queue each sign on the player (with logs)
        Debug.Log($"[PipelineTester] Queuing {sequence.signs.Count} sign(s): {string.Join(", ", sequence.signs.ConvertAll(s => s.glossLabel))}");
        foreach (LandmarkSeries sign in sequence.signs) player.Enqueue(sign);
    }
}