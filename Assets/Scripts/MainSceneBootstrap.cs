using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

// Loads the main scene additively since it originally was not visible when running together with VoxignMobileUI
public class MainSceneBootstrap : MonoBehaviour
{
    [SerializeField] private string mainSceneName = "Main";

    IEnumerator Start()
    {
        yield return null;

        if (string.IsNullOrEmpty(mainSceneName)) yield break;
        if (SceneManager.GetSceneByName(mainSceneName).isLoaded) yield break;

        SceneManager.LoadSceneAsync(mainSceneName, LoadSceneMode.Additive);
        Debug.Log($"[MainSceneBootstrap] Loading '{mainSceneName}' additively.");
    }
}
