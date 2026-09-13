using System;
using System.Collections.Generic;
using UnityEngine;
#if DYR_META_SDK
using Meta.XR.BuildingBlocks;
using Meta.XR.MRUtilityKit;
using UnityEngine.Events;
#endif

/// <summary>Everything that touches the Meta XR SDK lives here, and nothing else does.</summary>
public class DyrMetaBridge : MonoBehaviour
{
    const string Tag = "meta";

#if DYR_META_SDK
    public const bool SdkAvailable = true;
#else
    public const bool SdkAvailable = false;
#endif

    static DyrMetaBridge _instance;

    /// <summary>The single instance, created the first time it is needed.</summary>
    public static DyrMetaBridge Instance
    {
        get
        {
            if (_instance == null)
            {
                GameObject host = new GameObject("DyrMetaBridge");
                _instance = host.AddComponent<DyrMetaBridge>();
                DontDestroyOnLoad(host);
            }
            return _instance;
        }
    }


    /// <summary>
    /// What the room scan actually contains, logged once so a session explains its own results.
    /// </summary>
    /// <remarks>
    /// Scene understanding recognises a fixed set of about eighteen categories — floor, walls,
    /// table, couch, storage, bed, screen, lamp, plant, wall art, door and window frames — and
    /// labels nothing else. A model that names a mug or a laptop is naming something the room
    /// scan never saw, so its label has no surface to sit on and hangs at fallback distance.
    /// The global mesh, when the scan produced one, covers everything else.
    /// </remarks>
    public static void LogRoomContents()
    {
#if DYR_META_SDK
        MRUKRoom room = MRUK.Instance != null ? MRUK.Instance.GetCurrentRoom() : null;
        if (room == null)
        {
            DyrLog.Warn(Tag, "No MRUK room: labels cannot land on anything and will hang in the "
                + "air. Run Settings > Environment setup > Space setup in the headset.");
            return;
        }

        var counts = new System.Collections.Generic.Dictionary<string, int>();
        foreach (MRUKAnchor anchor in room.Anchors)
        {
            if (anchor == null) continue;
            string name = anchor.Label.ToString();
            counts.TryGetValue(name, out int seen);
            counts[name] = seen + 1;
        }

        var parts = new System.Text.StringBuilder();
        foreach (var entry in counts) parts.Append(entry.Key).Append(" x").Append(entry.Value).Append("  ");

        bool hasGlobalMesh = room.GetGlobalMeshAnchor() != null;
        DyrLog.Info(Tag, "Room scan holds " + room.Anchors.Count + " anchor(s): "
            + (parts.Length > 0 ? parts.ToString() : "none")
            + "| global mesh: " + (hasGlobalMesh ? "YES - labels can land anywhere"
                : "NO - labels can only land on the categories above; everything else hangs in "
                + "the air. Re-run Space setup and let it scan the room to build one."));
#endif
    }

    /// <summary>Where a camera ray meets the real world.</summary>
    public static Vector3 ProjectToWorld(
        Ray ray, float fallbackDistance, out bool hitRealSurface, out Transform hitObject)
    {
        hitRealSurface = false;
        hitObject = null;
        float reach = fallbackDistance * 4f;

#if DYR_META_SDK
        MRUKRoom room = MRUK.Instance != null ? MRUK.Instance.GetCurrentRoom() : null;
        if (room != null)
        {
            RaycastHit mrukHit;
            MRUKAnchor mrukAnchor;
            if (room.Raycast(ray, reach, out mrukHit, out mrukAnchor))
            {
                hitRealSurface = true;
                // Use the MRUK anchor for both placement and identity.
                hitObject = mrukAnchor != null ? mrukAnchor.transform : null;
                return mrukHit.point;
            }
        }
#endif

        RaycastHit hit;
        if (Physics.Raycast(ray, out hit, reach, ~0, QueryTriggerInteraction.Ignore))
        {
            hitRealSurface = true;
            // The COLLIDER's transform: hit.transform returns the rigidbody's, i.e. the room root.
            hitObject = hit.collider != null ? hit.collider.transform : hit.transform;
            return hit.point;
        }
        return ray.origin + ray.direction * fallbackDistance;
    }

    /// <summary>The MRUK room UUID, so objects are grouped per physical room.</summary>
    public static string CurrentRoomId()
    {
#if DYR_META_SDK
        MRUKRoom room = MRUK.Instance != null ? MRUK.Instance.GetCurrentRoom() : null;
        if (room != null && room.Anchor != null) return room.Anchor.Uuid.ToString();
#endif
        return "";
    }

    /// <summary>Is a headset with a usable passthrough camera actually present right now?</summary>
    public static bool PassthroughAvailable
    {
        get
        {
#if DYR_META_SDK
            // isHmdPresent is INSIDE the try: on a machine with no headset the OpenXR runtime
            // throws from it, and thrown out of here it killed the scan loop before it had a source.
            try
            {
                if (!OVRManager.isHmdPresent) return false;
                if (!OVRManager.IsInsightPassthroughSupported()) return false;
            }
            catch (Exception error)
            {
                DyrLog.Info(Tag, "Asked whether passthrough is available and the Meta runtime threw ("
                    + error.Message + ") -> taken as a no");
                return false;   // runtime not ready or extension absent
            }
            return true;
#else
            return false;
#endif
        }
    }

    /// <summary>Whether this machine can actually save spatial anchors.</summary>
    public static bool SpatialAnchorsAvailable
    {
        get
        {
#if DYR_META_SDK
            try
            {
                // isHmdPresent inside the try for the same reason as above: it throws with no headset.
                if (!OVRManager.isHmdPresent) return false;
                OVRPlugin.SystemHeadset headset = OVRPlugin.GetSystemHeadsetType();
                return headset != OVRPlugin.SystemHeadset.None
                    && headset != OVRPlugin.SystemHeadset.Oculus_Quest
                    && headset != OVRPlugin.SystemHeadset.Oculus_Link_Quest;
            }
            catch (Exception)
            {
                return false;
            }
#else
            return false;
#endif
        }
    }


    /// <summary>Create a spatial anchor at a point and persist it on the headset.</summary>
    public void CreateAnchor(Vector3 position, Action<string, Transform> onDone)
    {
#if DYR_META_SDK
        if (SpatialAnchorsAvailable)
        {
            EnqueueCreate(position, onDone);
            return;
        }
#endif
        // Fall back to a session-only transform.
        GameObject holder = new GameObject("DyrAnchor (session only)");
        holder.transform.position = position;
        onDone?.Invoke("", holder.transform);
    }

    /// <summary>Restore previously saved anchors.</summary>
    public void LoadAnchors(List<string> uuids, Action<Dictionary<string, Transform>> onDone)
    {
#if DYR_META_SDK
        if (SpatialAnchorsAvailable && uuids != null && uuids.Count > 0)
        {
            LoadSaved(uuids, onDone);
            return;
        }
#endif
        if (uuids != null && uuids.Count > 0)
            DyrLog.Info(Tag, uuids.Count + " object(s) have saved anchors, but spatial anchors are "
                + "not available here" + (SdkAvailable ? " (no headset)" : " (DYR_META_SDK not defined)")
                + " -> their labels cannot be restored in place.");
        onDone?.Invoke(new Dictionary<string, Transform>());
    }

#if DYR_META_SDK
    SpatialAnchorCoreBuildingBlock _anchorCore;
    GameObject _template;
    // Serialize requests because the SDK exposes one shared completion event.
    readonly Queue<KeyValuePair<Vector3, Action<string, Transform>>> _pending =
        new Queue<KeyValuePair<Vector3, Action<string, Transform>>>();
    Action<string, Transform> _creatingCallback;
    Action<Dictionary<string, Transform>> _loadingCallback;
    List<string> _loadingUuids;

    SpatialAnchorCoreBuildingBlock AnchorCore()
    {
        if (_anchorCore != null) return _anchorCore;

        _anchorCore = GetComponent<SpatialAnchorCoreBuildingBlock>();
        if (_anchorCore == null) _anchorCore = gameObject.AddComponent<SpatialAnchorCoreBuildingBlock>();

        // Runtime components need explicit UnityEvent instances.
        if (_anchorCore.OnAnchorCreateCompleted == null)
            _anchorCore.OnAnchorCreateCompleted =
                new UnityEvent<OVRSpatialAnchor, OVRSpatialAnchor.OperationResult>();
        if (_anchorCore.OnAnchorsLoadCompleted == null)
            _anchorCore.OnAnchorsLoadCompleted = new UnityEvent<List<OVRSpatialAnchor>>();
        _anchorCore.OnAnchorCreateCompleted.AddListener(OnCreateCompleted);
        _anchorCore.OnAnchorsLoadCompleted.AddListener(OnLoadCompleted);
        return _anchorCore;
    }

    GameObject Template()
    {
        if (_template == null)
        {
            // Keep the template active and away from visible geometry.
            _template = new GameObject("DyrAnchorTemplate");
            _template.transform.position = new Vector3(0f, -1000f, 0f);
        }
        return _template;
    }

    void EnqueueCreate(Vector3 position, Action<string, Transform> onDone)
    {
        _pending.Enqueue(new KeyValuePair<Vector3, Action<string, Transform>>(position, onDone));
        if (_creatingCallback == null) StartNextCreate();
    }

    void StartNextCreate()
    {
        if (_pending.Count == 0) return;
        KeyValuePair<Vector3, Action<string, Transform>> request = _pending.Dequeue();
        _creatingCallback = request.Value;
        AnchorCore().InstantiateSpatialAnchor(Template(), request.Key, Quaternion.identity);
    }

    void OnCreateCompleted(OVRSpatialAnchor anchor, OVRSpatialAnchor.OperationResult result)
    {
        Action<string, Transform> callback = _creatingCallback;
        _creatingCallback = null;

        if (anchor == null || result != OVRSpatialAnchor.OperationResult.Success)
        {
            DyrLog.Warn(Tag, "Spatial anchor creation FAILED (" + result + ") -> the label will show "
                + "but will not come back next session.");
            callback?.Invoke("", null);
        }
        else
        {
            string uuid = anchor.Uuid.ToString();
            anchor.gameObject.name = "DyrAnchor " + uuid.Substring(0, 8);
            DyrLog.Info(Tag, "Spatial anchor " + uuid + " saved at "
                + anchor.transform.position.ToString("F2"));
            callback?.Invoke(uuid, anchor.transform);
        }
        StartNextCreate();
    }

    void LoadSaved(List<string> uuids, Action<Dictionary<string, Transform>> onDone)
    {
        var ids = new List<Guid>();
        foreach (string uuid in uuids)
        {
            Guid id;
            if (Guid.TryParse(uuid, out id)) ids.Add(id);
        }
        if (ids.Count == 0)
        {
            onDone?.Invoke(new Dictionary<string, Transform>());
            return;
        }
        _loadingCallback = onDone;
        _loadingUuids = uuids;
        DyrLog.Info(Tag, "Localizing " + ids.Count + " saved spatial anchor(s) ...");
        AnchorCore().LoadAndInstantiateAnchors(Template(), ids);
    }

    void OnLoadCompleted(List<OVRSpatialAnchor> anchors)
    {
        Action<Dictionary<string, Transform>> callback = _loadingCallback;
        _loadingCallback = null;

        var restored = new Dictionary<string, Transform>();
        if (anchors != null)
        {
            foreach (OVRSpatialAnchor anchor in anchors)
            {
                if (anchor == null) continue;
                restored[anchor.Uuid.ToString()] = anchor.transform;
            }
        }
        int asked = _loadingUuids != null ? _loadingUuids.Count : restored.Count;
        DyrLog.Info(Tag, "Localized " + restored.Count + "/" + asked + " spatial anchor(s)"
            + (restored.Count < asked ? " - the rest belong to another room" : ""));
        callback?.Invoke(restored);
    }
#endif
}
