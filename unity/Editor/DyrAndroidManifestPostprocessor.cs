#if UNITY_EDITOR && UNITY_ANDROID
using System.IO;
using System.Xml;
using UnityEditor.Android;

/// <summary>The headset-camera permission, log storage, and the cleartext HTTP Android 9+ refuses.</summary>
public sealed class DyrAndroidManifestPostprocessor : IPostGenerateGradleAndroidProject
{
    const string AndroidNamespace = "http://schemas.android.com/apk/res/android";
    const string CameraPermission = "horizonos.permission.HEADSET_CAMERA";
    const string WriteStoragePermission = "android.permission.WRITE_EXTERNAL_STORAGE";
    const string ReadStoragePermission = "android.permission.READ_EXTERNAL_STORAGE";

    public int callbackOrder { get { return 100; } }

    public void OnPostGenerateGradleAndroidProject(string path)
    {
        string manifestPath = Path.Combine(path, "src", "main", "AndroidManifest.xml");
        XmlDocument document = new XmlDocument();
        document.Load(manifestPath);
        XmlElement manifest = document.DocumentElement;
        EnsurePermission(document, manifest, CameraPermission);

        // The session log goes to shared storage: scoped storage hides Android/data from the
        // headset's own Files app, and reading it back there otherwise needs a data cable.
        EnsurePermission(document, manifest, WriteStoragePermission);
        EnsurePermission(document, manifest, ReadStoragePermission);

        EnsureCleartextTraffic(document, manifest);
        EnsureLegacyExternalStorage(document, manifest);
        document.Save(manifestPath);
    }

    /// <summary>Keep pre-scoped-storage paths working, so /sdcard/Documents stays writable.</summary>
    static void EnsureLegacyExternalStorage(XmlDocument document, XmlElement manifest)
    {
        XmlNamespaceManager namespaces = new XmlNamespaceManager(document.NameTable);
        namespaces.AddNamespace("android", AndroidNamespace);
        XmlElement application = manifest.SelectSingleNode("application", namespaces) as XmlElement;
        if (application == null) return;
        application.SetAttribute("requestLegacyExternalStorage", AndroidNamespace, "true");

        // Ignored from targetSdk 30 on, silently: the log never reaches the Files app.
        int target = (int)UnityEditor.PlayerSettings.Android.targetSdkVersion;
        if (target == 0 || target >= 30)
            UnityEngine.Debug.LogWarning("[DYR] targetSdk "
                + (target == 0 ? "(auto)" : target.ToString())
                + " ignores legacy storage: the log will not show in the headset's Files app. "
                + "Pull it with adb from Android/data.");
    }

    /// <summary>Let the build reach a plain-HTTP backend on the LAN.</summary>
    static void EnsureCleartextTraffic(XmlDocument document, XmlElement manifest)
    {
        XmlNamespaceManager namespaces = new XmlNamespaceManager(document.NameTable);
        namespaces.AddNamespace("android", AndroidNamespace);
        XmlElement application = manifest.SelectSingleNode("application", namespaces) as XmlElement;
        if (application == null)
        {
            UnityEngine.Debug.LogWarning("[DYR] No <application> in the generated manifest; "
                + "cleartext HTTP could not be enabled and scans will fail on the headset.");
            return;
        }

        application.SetAttribute("usesCleartextTraffic", AndroidNamespace, "true");

        // A network security config OUTRANKS the attribute above, so take it out: the Meta
        // checklist can add one, and the only symptom is a connection error on the headset.
        string securityConfig = application.GetAttribute("networkSecurityConfig", AndroidNamespace);
        if (!string.IsNullOrEmpty(securityConfig))
        {
            application.RemoveAttribute("networkSecurityConfig", AndroidNamespace);
            UnityEngine.Debug.LogWarning("[DYR] Removed networkSecurityConfig='" + securityConfig
                + "': it would have blocked the plain-HTTP backend.");
        }

        UnityEngine.Debug.Log("[DYR] Cleartext HTTP enabled: the build can reach the backend "
            + "at http://<pc-lan-ip>:8000");
    }

    static void EnsurePermission(XmlDocument document, XmlElement manifest, string name)
    {
        XmlNamespaceManager namespaces = new XmlNamespaceManager(document.NameTable);
        namespaces.AddNamespace("android", AndroidNamespace);
        string query = "uses-permission[@android:name='" + name + "']";
        if (manifest.SelectSingleNode(query, namespaces) != null) return;

        XmlElement element = document.CreateElement("uses-permission");
        element.SetAttribute("name", AndroidNamespace, name);
        manifest.PrependChild(element);
    }
}
#endif
