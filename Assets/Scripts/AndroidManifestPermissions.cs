#if UNITY_EDITOR && UNITY_ANDROID
using System.IO;
using System.Xml.Linq;
using UnityEditor;
using UnityEditor.Android;
using UnityEngine;

// Patch the generated Gradle manifest so the app can record audio and reach the backend over Websocket
public class AndroidManifestPermissions : IPostGenerateGradleAndroidProject
{
    private const string AndroidNamespace = "http://schemas.android.com/apk/res/android";

    public int callbackOrder { get { return 0; } }

    public void OnPostGenerateGradleAndroidProject(string basePath)
    {
        string manifestPath = Path.Combine(basePath, "src", "main", "AndroidManifest.xml");
        if (!File.Exists(manifestPath))
        {
            manifestPath = Path.Combine(basePath, "unityLibrary", "src", "main", "AndroidManifest.xml");
            if (!File.Exists(manifestPath))
            {
                Debug.LogWarning($"[AndroidManifestPermissions] Could not find AndroidManifest.xml under {basePath}.");
                return;
            }
        }

        XDocument document = XDocument.Load(manifestPath);
        XElement manifest = document.Root;
        if (manifest == null) return;

        XNamespace android = AndroidNamespace;
        EnsurePermission(manifest, android, "android.permission.INTERNET");
        EnsurePermission(manifest, android, "android.permission.RECORD_AUDIO");

        XElement application = manifest.Element("application");
        if (EditorUserBuildSettings.development && application != null && application.Attribute(android + "usesCleartextTraffic") == null)
        {
            // The backend is reached over plain ws:// if on the local network
            application.SetAttributeValue(android + "usesCleartextTraffic", "true");
        }

        document.Save(manifestPath);
        Debug.Log("[AndroidManifestPermissions] Android manifest permissions updated.");
    }

    // Ensures that a specified permission is present in the manifest, adding it if necessary
    private static void EnsurePermission(XElement manifest, XNamespace android, string permission)
    {
        foreach (XElement element in manifest.Elements("uses-permission"))
        {
            if ((string)element.Attribute(android + "name") == permission) return;
        }
        manifest.AddFirst(new XElement("uses-permission", new XAttribute(android + "name", permission)));
    }
}
#endif
