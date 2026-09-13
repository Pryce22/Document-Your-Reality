using UnityEngine;
using UnityEngine.Rendering;

/// <summary>The virtual room VR mode runs in.</summary>
public class DyrVrEnvironment : MonoBehaviour
{
    const string Tag = "vr-room";

    [Header("Room")]
    [Tooltip("Room prefab to instantiate. Leave empty to use the one already in the scene.")]
    [SerializeField] private GameObject roomPrefab;
    [Tooltip("Fallback when no prefab is assigned: a prefab with this name under a Resources folder.")]
    [SerializeField] private string roomResourcePath = "DyrVrRoom";
    [Tooltip("Last fallback: a root object already in the scene whose name starts with this.")]
    [SerializeField] private string roomObjectName = "LowPolyLivingRoomPack_Demo";

    [Header("Preparation")]
    [Tooltip("Add a box collider to every mesh that has none. OFF = labels cannot land on the furniture.")]
    [SerializeField] private bool addMissingColliders = true;
    [Tooltip("Build floor, walls and ceiling around the furniture when the prefab has none.")]
    [SerializeField] private bool buildRoomShell = true;
    [Tooltip("Move the camera rig to the middle of the room at floor level.")]
    [SerializeField] private bool movePlayerToRoom = true;

    /// <summary>The room in the scene, once resolved.</summary>
    public Transform Room { get; private set; }
    /// <summary>World bounds of the furniture, used for the shell and the spawn point.</summary>
    public Bounds RoomBounds { get; private set; }

    /// <summary>Add the environment to a host object unless one is already there.</summary>
    public static DyrVrEnvironment Ensure(GameObject host)
    {
        DyrVrEnvironment existing = FindAnyObjectByType<DyrVrEnvironment>();
        return existing != null ? existing : host.AddComponent<DyrVrEnvironment>();
    }

    void Awake()
    {
        Room = ResolveRoom();
        if (Room == null)
        {
            DyrLog.Warn(Tag, "No room found: assign roomPrefab, or put a prefab named "
                + roomResourcePath + " in a Resources folder, or drop a room in the scene named "
                + roomObjectName + ". Only an empty shell will be built.");
        }

        RoomBounds = MeasureRoom();
        if (Room != null && addMissingColliders) AddColliders(Room);
        if (buildRoomShell) BuildShell();
        EnsureLight();
        if (movePlayerToRoom) MovePlayer();
    }


    Transform ResolveRoom()
    {
        if (roomPrefab != null)
        {
            GameObject instance = Instantiate(roomPrefab);
            instance.name = roomPrefab.name;
            DyrLog.Info(Tag, "Room instantiated from the assigned prefab " + roomPrefab.name);
            return instance.transform;
        }

        if (!string.IsNullOrEmpty(roomResourcePath))
        {
            GameObject fromResources = Resources.Load<GameObject>(roomResourcePath);
            if (fromResources != null)
            {
                GameObject instance = Instantiate(fromResources);
                instance.name = fromResources.name;
                DyrLog.Info(Tag, "Room instantiated from Resources/" + roomResourcePath);
                return instance.transform;
            }
        }

        // Already in the scene, and DEACTIVATED: that is how the one shared scene serves all three modes.
        if (!string.IsNullOrEmpty(roomObjectName))
        {
            foreach (GameObject root in gameObject.scene.GetRootGameObjects())
            {
                if (!root.name.StartsWith(roomObjectName)) continue;
                if (!root.activeSelf) root.SetActive(true);
                DyrLog.Info(Tag, "Using the room already in the scene: " + root.name
                    + " (switched on for VR mode)");
                return root.transform;
            }
        }
        return null;
    }

    /// <summary>Bounds of everything the room draws, in world space.</summary>
    Bounds MeasureRoom()
    {
        // A default a person fits in, for the case where there is no room at all.
        Bounds bounds = new Bounds(Vector3.zero, new Vector3(6f, 2.6f, 6f));
        if (Room == null) return bounds;

        Renderer[] renderers = Room.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) return bounds;

        bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
        DyrLog.Info(Tag, "Room measured: " + renderers.Length + " renderer(s), "
            + bounds.size.ToString("F1") + " m around " + bounds.center.ToString("F1"));
        return bounds;
    }


    /// <summary>Nothing a label is about is this big twice over. Mirrors backgroundSizeMeters.</summary>
    const float MaxObjectMeters = 3f;

    /// <summary>A box collider per mesh that lacks one; a room-sized one would stop every ray.</summary>
    void AddColliders(Transform root)
    {
        int added = 0;
        int skipped = 0;
        MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>();
        foreach (MeshFilter filter in filters)
        {
            if (filter.sharedMesh == null) continue;
            if (filter.GetComponent<Collider>() != null) continue;

            Vector3 size = Vector3.Scale(filter.sharedMesh.bounds.size, filter.transform.lossyScale);
            int wide = 0;
            if (Mathf.Abs(size.x) > MaxObjectMeters) wide++;
            if (Mathf.Abs(size.y) > MaxObjectMeters) wide++;
            if (Mathf.Abs(size.z) > MaxObjectMeters) wide++;
            if (wide >= 2)
            {
                DyrLog.Info(Tag, "'" + filter.name + "' is " + size.ToString("F1")
                    + " m: that is the room, not a thing in it -> no collider, so rays reach "
                    + "what is inside it");
                skipped++;
                continue;
            }

            BoxCollider box = filter.gameObject.AddComponent<BoxCollider>();
            box.center = filter.sharedMesh.bounds.center;
            box.size = filter.sharedMesh.bounds.size;
            added++;
        }
        DyrLog.Info(Tag, "Colliders: " + added + " added over " + filters.Length
            + " mesh(es)" + (skipped > 0 ? ", " + skipped + " room-sized mesh(es) left open" : "")
            + " -> labels can now land on the furniture");
    }

    /// <summary>Floor, four walls and a ceiling around the furniture.</summary>
    void BuildShell()
    {
        if (GameObject.Find("DyrVrRoomShell") != null) return;

        Bounds b = RoomBounds;
        const float margin = 0.6f;
        const float thickness = 0.1f;
        float width = b.size.x + margin * 2f;
        float depth = b.size.z + margin * 2f;
        float height = Mathf.Max(2.6f, b.size.y + 0.4f);
        Vector3 c = new Vector3(b.center.x, b.min.y, b.center.z);

        // Built for this run only: a scene saved while it stands must not keep a second room.
        GameObject shell = new GameObject("DyrVrRoomShell");
        shell.hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild;
        Material wall = MakeMaterial(new Color(0.86f, 0.85f, 0.82f));
        Material floor = MakeMaterial(new Color(0.55f, 0.48f, 0.42f));

        AddSlab(shell.transform, "Floor", c + new Vector3(0f, -thickness * 0.5f, 0f),
            new Vector3(width, thickness, depth), floor);
        AddSlab(shell.transform, "Ceiling", c + new Vector3(0f, height + thickness * 0.5f, 0f),
            new Vector3(width, thickness, depth), wall);
        AddSlab(shell.transform, "Wall -Z", c + new Vector3(0f, height * 0.5f, -depth * 0.5f),
            new Vector3(width, height, thickness), wall);
        AddSlab(shell.transform, "Wall +Z", c + new Vector3(0f, height * 0.5f, depth * 0.5f),
            new Vector3(width, height, thickness), wall);
        AddSlab(shell.transform, "Wall -X", c + new Vector3(-width * 0.5f, height * 0.5f, 0f),
            new Vector3(thickness, height, depth), wall);
        AddSlab(shell.transform, "Wall +X", c + new Vector3(width * 0.5f, height * 0.5f, 0f),
            new Vector3(thickness, height, depth), wall);

        // A closed box would put its own interior in shadow and the model would be handed a dark room.
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.55f, 0.56f, 0.60f);

        DyrLog.Info(Tag, "Room shell built: " + width.ToString("F1") + " x " + depth.ToString("F1")
            + " x " + height.ToString("F1") + " m, ambient light raised so the interior is lit");
    }

    static void AddSlab(Transform parent, string name, Vector3 center, Vector3 size, Material material)
    {
        GameObject slab = GameObject.CreatePrimitive(PrimitiveType.Cube);
        slab.name = name;
        slab.transform.SetParent(parent, false);
        slab.transform.position = center;
        slab.transform.localScale = size;
        Renderer renderer = slab.GetComponent<Renderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
    }

    /// <summary>URP is the pipeline here; the fallback keeps the scene from going magenta.</summary>
    public static Material MakeMaterial(Color colour)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        Material material = new Material(shader);
        // URP Lit has no _Color: setting Material.color there logs a warning and changes nothing.
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", colour);
        else material.color = colour;
        return material;
    }

    void EnsureLight()
    {
        foreach (Light light in FindObjectsByType<Light>(FindObjectsSortMode.None))
            if (light.type == LightType.Directional && light.enabled) return;

        GameObject sun = new GameObject("DyrVrSun");
        sun.hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild;
        Light lamp = sun.AddComponent<Light>();
        lamp.type = LightType.Directional;
        lamp.intensity = 1.1f;
        lamp.shadows = LightShadows.Soft;
        sun.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        DyrLog.Info(Tag, "No directional light in the scene -> one added "
            + "(an unlit room reads as nothing to the model)");
    }


    /// <summary>Put the wearer in the room.</summary>
    void MovePlayer()
    {
        Camera cam = Camera.main;
        if (cam == null)
        {
            DyrLog.Warn(Tag, "No Camera.main yet -> the rig was left where it is. "
                + "Tag CenterEyeAnchor as MainCamera.");
            return;
        }

        Transform rig = cam.transform;
        while (rig.parent != null) rig = rig.parent;

        Bounds b = RoomBounds;
        // Start with the furniture in front of the wearer.
        Vector3 spawn = new Vector3(b.center.x, b.min.y, b.center.z - b.extents.z * 0.55f);
        rig.SetPositionAndRotation(spawn, Quaternion.identity);
        DyrLog.Info(Tag, "Rig " + rig.name + " moved to " + spawn.ToString("F2") + " facing the room");
    }
}
