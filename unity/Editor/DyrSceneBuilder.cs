using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Builds and validates the project scene.</summary>
public static class DyrSceneBuilder
{
    const string ScenePath = "Assets/Scenes/DocumentYourReality.unity";
    const string RoomPrefabName = "LowPolyLivingRoomPack_Demo";
    const string MrukObjectName = "MR Utility Kit";
    const string MetaDefine = "DYR_META_SDK";


    [MenuItem("Tools/DYR/Create Scene", false, 10)]
    public static void CreateScene()
    {
        // Batch mode cannot display the confirmation dialog.
        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        AddCameraRig();
        ConfigureMetaProject();
        AddMruk();
        AddRoom();
        AddLight();
        AddDocumentReality();

        System.IO.Directory.CreateDirectory("Assets/Scenes");
        EditorSceneManager.SaveScene(scene, ScenePath);
        SetAsFirstBuildScene(ScenePath);
        AssetDatabase.Refresh();

        Debug.Log("[DYR] Scene created at " + ScenePath + ". Set DocumentReality > "
            + "DyrScanLoop > Mode to Auto (passthrough on headset, video on PC), Vr for the "
            + "virtual room, or Video to force the clip.");
        Verify();
    }

    static void AddCameraRig()
    {
        GameObject rigPrefab = FindPrefab("OVRCameraRig");
        if (rigPrefab == null)
        {
            // No Meta SDK: a plain camera still runs Video and Vr modes, flat on the monitor.
            GameObject host = new GameObject("Main Camera");
            host.tag = "MainCamera";
            host.transform.position = new Vector3(0f, 1.6f, 0f);
            host.AddComponent<Camera>();
            host.AddComponent<AudioListener>();
            Debug.LogWarning("[DYR] OVRCameraRig prefab not found; added a basic camera. "
                + "Import the Meta XR SDK and recreate the scene for headset or simulator use.");
            return;
        }

        GameObject rig = (GameObject)PrefabUtility.InstantiatePrefab(rigPrefab);
        rig.transform.position = Vector3.zero;

        // Exactly one MainCamera.
        Tag(rig, "CenterEyeAnchor", "MainCamera");
        Tag(rig, "LeftEyeAnchor", "Untagged");
        Debug.Log("[DYR] Added OVRCameraRig; only CenterEyeAnchor is tagged MainCamera");
    }

    static void Tag(GameObject rig, string childName, string tag)
    {
        Transform child = FindDeep(rig.transform, childName);
        if (child == null)
        {
            // Silently skipped, this leaves Camera.main null and the session shows nothing:
            // no status panel, and no pose for a capture.
            Debug.LogError("[DYR] '" + childName + "' is not in the rig, so it could not be tagged '"
                + tag + "'. If this was CenterEyeAnchor, Camera.main will be null and neither the "
                + "status panel nor a capture pose will work.");
            return;
        }
        if (child.gameObject.CompareTag(tag)) return;
        child.gameObject.tag = tag;
        // Record prefab overrides before saving the scene.
        PrefabUtility.RecordPrefabInstancePropertyModifications(child.gameObject);
        Debug.Log("[DYR] Tagged '" + childName + "' as '" + tag + "'");
    }

    /// <summary>MR Utility Kit: the room geometry a Passthrough label has to land on.</summary>
    static void AddMruk()
    {
        if (SceneHasObjectNamed(MrukObjectName)) return;

        GameObject prefab = FindPrefab("MRUK");
        if (prefab == null)
        {
            Debug.LogWarning("[DYR] MRUK prefab not found; Passthrough labels will have no real "
                + "geometry to land on. Install the MR Utility Kit package "
                + "(com.meta.xr.mrutilitykit).");
            return;
        }
        GameObject mruk = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        mruk.name = MrukObjectName;
        Debug.Log("[DYR] Added '" + mruk.name + "': Passthrough labels can now be placed on the "
            + "real room instead of at a guessed depth.");
    }

    /// <summary>Update a scene without rebuilding it, so tuned Inspector values survive.</summary>
    [MenuItem("Tools/DYR/Add Meta Support to This Scene", false, 11)]
    public static void AddMetaSupportToScene()
    {
        ConfigureMetaProject();
        AddMruk();
        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        Debug.Log("[DYR] Save the scene to keep this.");
        Verify();
    }

    /// <summary>The virtual room, switched OFF.</summary>
    static void AddRoom()
    {
        GameObject roomPrefab = FindPrefab(RoomPrefabName);
        if (roomPrefab == null)
        {
            Debug.LogWarning("[DYR] Prefab '" + RoomPrefabName + "' not found; Vr mode will "
                + "show only the room shell. Import LowPolyLivingRoomPack.");
            return;
        }
        GameObject room = (GameObject)PrefabUtility.InstantiatePrefab(roomPrefab);
        room.transform.position = Vector3.zero;
        room.SetActive(false);
        Debug.Log("[DYR] Added disabled room '" + room.name + "'; only Vr mode enables it");
    }

    static void AddLight()
    {
        GameObject sun = new GameObject("Directional Light");
        Light light = sun.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.1f;
        // Hard, not soft: a stereo view pays for shadows twice and on low-poly furniture the difference is not visible.
        light.shadows = LightShadows.Hard;
        sun.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
    }

    static void AddDocumentReality()
    {
        GameObject host = new GameObject("DocumentReality");
        host.AddComponent<DyrScanLoop>();
        Debug.Log("[DYR] Created DocumentReality (Mode = Auto)");
    }

    /// <summary>The only scene in the build.</summary>
    static void SetAsFirstBuildScene(string path)
    {
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(path, true) };
    }


    [MenuItem("Tools/DYR/Validate Scene", false, 20)]
    public static void Verify()
    {
        List<string> problems = new List<string>();

        if (Object.FindAnyObjectByType<DyrScanLoop>() == null)
            problems.Add("no DyrScanLoop in the scene; the system has no driver");

        Camera[] mains = Object.FindObjectsByType<Camera>(FindObjectsSortMode.None);
        int tagged = 0;
        foreach (Camera cam in mains) if (cam.CompareTag("MainCamera")) tagged++;
        if (tagged == 0)
            problems.Add("no camera tagged MainCamera; world labels cannot be placed");
        else if (tagged > 1)
            problems.Add(tagged + " cameras tagged MainCamera; Camera.main is ambiguous and labels "
                + "use the wrong eye (remove the tag from LeftEyeAnchor)");

        if (FindPrefab(RoomPrefabName) == null)
            problems.Add("prefab '" + RoomPrefabName + "' not found; Vr mode will be empty");

        // Left at NotAllowed, every UnityWebRequest to the http:// backend throws
        // InvalidOperationException before a packet is sent, and the only symptom is a session
        // that discovers the backend and then never calls it.
        if (PlayerSettings.insecureHttpOption == InsecureHttpOption.NotAllowed)
            problems.Add("Player Settings > Allow downloads over HTTP is 'Not allowed'; every "
                + "backend call will throw InvalidOperationException. Set it to 'Always allowed' "
                + "(the backend is plain HTTP on the LAN)");

        // Validate the clip selected in the scene.
        DyrScanLoop loop = Object.FindAnyObjectByType<DyrScanLoop>();
        string clip = loop != null ? loop.VideoFileName : "";
        if (string.IsNullOrEmpty(clip))
            problems.Add("DyrScanLoop has no video configured; Video mode cannot start");
        else if (!System.IO.File.Exists("Assets/StreamingAssets/" + clip))
            problems.Add("Assets/StreamingAssets/" + clip + " is missing; Video mode cannot start");

        // By name, so this editor utility also compiles without MRUK.
        if (!SceneHasObjectNamed(MrukObjectName))
            problems.Add("MRUK is not in the scene; MR mode cannot resolve real geometry. "
                + "Add MR Utility Kit from Meta > Tools > Building Blocks");

        if (!IsMetaDefineOn(NamedBuildTarget.Android))
            Debug.Log("[DYR] Note: DYR_META_SDK is not defined for Android. Video and Vr still work; "
                + "the define enables passthrough, spatial anchors, and controller A/B input.");

        if (problems.Count == 0) { Debug.Log("[DYR] Scene validation passed."); return; }
        foreach (string problem in problems) Debug.LogWarning("[DYR] Validation: " + problem);
    }


    [MenuItem("Tools/DYR/Enable Meta Support (DYR_META_SDK)", false, 30)]
    public static void EnableMetaDefine() { SetMetaDefine(true); }

    [MenuItem("Tools/DYR/Disable Meta Support (DYR_META_SDK)", false, 31)]
    public static void DisableMetaDefine() { SetMetaDefine(false); }

    static void SetMetaDefine(bool enabled)
    {
        SetMetaDefine(NamedBuildTarget.Standalone, enabled);
        SetMetaDefine(NamedBuildTarget.Android, enabled);
        Debug.Log("[DYR] " + MetaDefine + (enabled ? " enabled" : " disabled")
            + " for Standalone and Android. Unity is recompiling scripts.");
    }

    static void SetMetaDefine(NamedBuildTarget target, bool enabled)
    {
        List<string> defines = new List<string>(PlayerSettings.GetScriptingDefineSymbols(target).Split(';'));
        defines.RemoveAll(string.IsNullOrEmpty);
        if (enabled == defines.Contains(MetaDefine)) return;
        if (enabled) defines.Add(MetaDefine); else defines.Remove(MetaDefine);
        PlayerSettings.SetScriptingDefineSymbols(target, string.Join(";", defines));
    }

    static bool IsMetaDefineOn(NamedBuildTarget target)
    {
        return (";" + PlayerSettings.GetScriptingDefineSymbols(target) + ";").Contains(";" + MetaDefine + ";");
    }

    static void ConfigureMetaProject()
    {
        string[] guids = AssetDatabase.FindAssets("OculusProjectConfig t:ScriptableObject");
        if (guids.Length == 0)
        {
            Debug.Log("[DYR] Meta project config not found; Video and Vr remain available. "
                + "Install Meta XR Core and MRUK before using MR mode.");
            return;
        }

        Object asset = AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GUIDToAssetPath(guids[0]));
        if (asset == null)
        {
            Debug.LogWarning("[DYR] OculusProjectConfig could not be loaded");
            return;
        }

        SerializedObject config = new SerializedObject(asset);
        SerializedProperty cameraAccess = config.FindProperty("isPassthroughCameraAccessEnabled");
        if (cameraAccess == null || cameraAccess.propertyType != SerializedPropertyType.Boolean)
        {
            Debug.LogWarning("[DYR] This Meta SDK does not expose Passthrough Camera Access. "
                + "MR mode requires MRUK v81 or newer.");
            return;
        }

        SerializedProperty display = config.FindProperty("insightPassthroughEnabled");
        bool changed = false;
        if (display != null && display.propertyType == SerializedPropertyType.Boolean
            && !display.boolValue)
        {
            display.boolValue = true;
            changed = true;
        }
        if (!cameraAccess.boolValue)
        {
            cameraAccess.boolValue = true;
            changed = true;
        }

        if (changed)
        {
            config.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
        }
        Debug.Log("[DYR] Meta passthrough and Passthrough Camera Access are enabled");
    }


    static GameObject FindPrefab(string name)
    {
        // Searches packages too, which is where the Meta SDK prefabs live.
        foreach (string guid in AssetDatabase.FindAssets("\"" + name + "\" t:Prefab"))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (System.IO.Path.GetFileNameWithoutExtension(path) != name) continue;
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab != null) return prefab;
        }
        return null;
    }

    /// <summary>Is there a root object whose name contains this?</summary>
    static bool SceneHasObjectNamed(string fragment)
    {
        foreach (GameObject root in UnityEngine.SceneManagement.SceneManager
            .GetActiveScene().GetRootGameObjects())
            if (root.name.Contains(fragment)) return true;
        return false;
    }

    static Transform FindDeep(Transform root, string name)
    {
        if (root.name == name) return root;
        for (int i = 0; i < root.childCount; i++)
        {
            Transform found = FindDeep(root.GetChild(i), name);
            if (found != null) return found;
        }
        return null;
    }
}
