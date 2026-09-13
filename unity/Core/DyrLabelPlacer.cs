using System.Collections.Generic;
using UnityEngine;

/// <summary>Places labels in screen or world space for the active source.</summary>
public class DyrLabelPlacer : MonoBehaviour
{
    const string Tag = "labels";

    [SerializeField] private DyrBackendClient client;

    [Header("World placement")]
    [Tooltip("Where a label goes when nothing real is hit by the ray (metres).")]
    [SerializeField] private float fallbackDistanceMeters = 2.0f;
    [Tooltip("Lift above the point the label was anchored to, so it does not sit inside the object.")]
    [SerializeField] private float labelHeightMeters = 0.12f;
    [Tooltip("Extra lift per label already at that spot, so they stack instead of covering each other.")]
    [SerializeField] private float labelStackMeters = 0.22f;
    [Tooltip("How close two world labels must be, ignoring height, to count as the same spot.")]
    [SerializeField] private float labelClearanceMeters = 0.35f;
    [Tooltip("Create and save a Meta Spatial Anchor per object. Off = labels last only this session.")]
    [SerializeField] private bool persistWithSpatialAnchors = true;
    [Tooltip("World labels farther than this are hidden. The model room is about nine metres across.")]
    // Generous: a label that vanishes because you stepped back looks like one that was never made.
    [SerializeField] private float maxVisibleDistanceMeters = 30f;
    [Tooltip("How many world labels at once. The nearest win; the rest return as you walk up. 0 = no limit.")]
    // No cap: with one, the ninth object silently replaced a label already up, which from inside
    // the headset reads as labelling having stopped.
    [SerializeField] private int maxVisibleLabels;
    [Tooltip("A collider this big in two directions is scenery: labels land there but do not attach.")]
    [SerializeField] private float backgroundSizeMeters = 3f;

    [Tooltip("Move a label when a later scan finds its object somewhere else. Off = labels stay "
        + "where they were first placed, even after the object has been picked up.")]
    [SerializeField] private bool followMovedObjects = true;
    [Tooltip("Metres an object must have travelled before its label follows. Below this, the "
        + "difference is measurement noise and moving the label only makes it twitch.")]
    [SerializeField] private float moveThresholdMeters = 0.25f;

    [Header("Screen placement")]
    [Tooltip("Seconds a screen label stays up before it fades out by itself. The picture behind it "
        + "never stops, so a label that outstays this is over the wrong part of the room.")]
    [SerializeField] private float labelHoldSeconds = 5f;
    [Tooltip("Draw the detection rectangle as well as the label, to check the model's boxes.")]
    [SerializeField] private bool showDetectionBoxes;


    const float LabelGapPx = 6f;

    readonly Dictionary<string, DyrLabelView> _views = new Dictionary<string, DyrLabelView>();
    readonly List<DyrLabelView> _screenViews = new List<DyrLabelView>();
    readonly List<DyrLabelView> _visibleCandidates = new List<DyrLabelView>();
    /// <summary>Spots claimed while an anchor is created, so two labels cannot overlap.</summary>
    readonly Dictionary<string, Vector3> _claimedPoints = new Dictionary<string, Vector3>();
    Transform _screenCanvas;
    /// <summary>Bumped on a source change: anchors still in flight for the old world drop themselves.</summary>
    int _generation;
    IDyrFrameSource _source;
    Rect _frameRect;
    bool _screenMode;

    public int VisibleLabelCount
    {
        get
        {
            int count = 0;
            foreach (DyrLabelView view in _views.Values)
                if (view != null && view.gameObject.activeSelf) count++;
            return count;
        }
    }

    void Awake()
    {
        if (client == null) client = FindAnyObjectByType<DyrBackendClient>();
    }


    /// <summary>A scan has started on <paramref name="source"/>.</summary>
    public void BeginScan(IDyrFrameSource source)
    {
        // Labels belong to the world they were placed in, so a source change clears the old ones.
        if (source != _source && _source != null)
        {
            DyrLog.Info(Tag, "Source changed to '" + source.SourceName + "' -> clearing the "
                + _views.Count + " label(s) of the previous one");
            ClearAll();
        }

        _source = source;
        _claimedPoints.Clear();
        // Initialize before the short-circuit assignment.
        Rect frameRect = new Rect();
        _screenMode = source != null && source.TryGetDisplayRect(out frameRect);
        _frameRect = frameRect;
    }

    /// <summary>Draw one label, now, without waiting for the rest of the scan.</summary>
    public void ApplyOne(ObjectLabelDto label, DyrCaptureView view)
    {
        if (label == null || string.IsNullOrEmpty(label.entity_id)) return;
        if (label.box == null || !label.box.IsValid)
        {
            DyrLog.Warn(Tag, "Label for " + label.entity_id + " has no usable box -> not placed. "
                + "The model returned a degenerate detection.");
            return;
        }

        if (_screenMode) PlaceOnScreen(label);
        else PlaceInWorld(label, view);
    }

    /// <summary>The session is over: nothing it drew is left standing in the scene.</summary>
    void OnDestroy()
    {
        ClearAll();
        if (_screenCanvas != null) Destroy(_screenCanvas.gameObject);
    }

    /// <summary>Destroy every label on screen.</summary>
    void ClearAll()
    {
        foreach (DyrLabelView view in _views.Values)
            if (view != null) Destroy(view.gameObject);
        _views.Clear();
        _claimedPoints.Clear();
        _generation++;
    }


    void PlaceOnScreen(ObjectLabelDto label)
    {
        DyrLabelView view = GetOrCreateScreenView(label);
        view.SetContent(label);
        view.NormalizedBox = new Rect(label.box.x0, label.box.y0,
                                      label.box.Width, label.box.Height);
        view.SetScreenTarget(ToScreenRect(view.NormalizedBox));
        view.ShowFor(labelHoldSeconds);
    }

    /// <summary>A normalised, top-down box to the pixels it covers on screen.</summary>
    Rect ToScreenRect(Rect box)
    {
        float x = _frameRect.x + box.x * _frameRect.width;
        float width = box.width * _frameRect.width;
        float yTop = _frameRect.y + _frameRect.height - box.y * _frameRect.height;
        float yBottom = _frameRect.y + _frameRect.height - (box.y + box.height) * _frameRect.height;
        return new Rect(x, yBottom, width, yTop - yBottom);
    }

    /// <summary>Keep every screen label over the part of the frame it describes.</summary>
    void UpdateScreenLabels()
    {
        if (_source == null || !_screenMode) return;

        Rect frameRect;
        if (!_source.TryGetDisplayRect(out frameRect)) return;
        _frameRect = frameRect;

        foreach (DyrLabelView view in _views.Values)
        {
            if (view == null || view.IsWorldSpace || !view.gameObject.activeSelf) continue;
            view.SetScreenTarget(ToScreenRect(view.NormalizedBox));
        }
    }

    DyrLabelView GetOrCreateScreenView(ObjectLabelDto label)
    {
        DyrLabelView view;
        if (_views.TryGetValue(label.entity_id, out view) && view != null) return view;

        if (_screenCanvas == null) _screenCanvas = BuildScreenCanvas();
        view = DyrLabelView.CreateScreenSpace(_screenCanvas, label.entity_id,
            ColourFor(label.entity_id), showDetectionBoxes);
        _views[label.entity_id] = view;
        DyrLog.Info(Tag, "Label created for " + label.entity_id + " '" + label.name + "'"
            + (label.is_new ? " (new object)" : " (already known -> re-shown)"));
        return view;
    }

    /// <summary>The overlay for 2D labels; flagged, or the editor saves it into the scene.</summary>
    static Transform BuildScreenCanvas()
    {
        GameObject canvasObject = new GameObject("DyrLabelCanvas");
        canvasObject.hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild;
        Canvas canvas = canvasObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 10;   // above the video canvas
        return canvasObject.transform;
    }

    /// <summary>Keep two labels from sitting on top of each other.</summary>
    void SpreadScreenLabels()
    {
        _screenViews.Clear();
        foreach (DyrLabelView view in _views.Values)
            if (view != null && !view.IsWorldSpace && view.gameObject.activeSelf)
                _screenViews.Add(view);
        if (_screenViews.Count == 0) return;

        _screenViews.Sort((a, b) => b.PanelBaseY.CompareTo(a.PanelBaseY));

        float ceiling = float.MaxValue;
        foreach (DyrLabelView view in _screenViews)
        {
            float top = view.PanelBaseY + view.PanelHeight;
            float offset = top > ceiling ? ceiling - top : 0f;
            view.SetPanelOffset(offset);
            ceiling = view.PanelBaseY + offset - LabelGapPx;
        }
    }

    public void HideScreenLabels()
    {
        foreach (DyrLabelView view in _views.Values)
            if (view != null && !view.IsWorldSpace) view.SetVisible(false);
    }


    void PlaceInWorld(ObjectLabelDto label, DyrCaptureView view)
    {
        DyrLabelView existing;
        if (_views.TryGetValue(label.entity_id, out existing) && existing != null)
        {
            existing.SetContent(label);
            // A recognised object comes back with a FRESH box, so a thing that has been moved
            // says so: its ray now lands somewhere else. Scene understanding cannot follow an
            // object, but each scan re-measures where it is, and the label can follow that.
            FollowIfMoved(label, view, existing);
            return;
        }
        if (_claimedPoints.ContainsKey(label.entity_id)) return;

        // Cast from the captured pose because inference returns later.
        if (!view.valid) view = DyrCaptureView.From(DyrCameras.Head);
        if (!view.valid)
        {
            DyrLog.Error(Tag, "No captured view and no Camera.main: cannot place a world label. "
                + "Tag the CenterEyeAnchor as MainCamera.");
            return;
        }

        bool hitReal;
        Vector3 point;
        Transform hitObject = ResolveObject(label.box, view, out point, out hitReal);
        // Scenery is where a label ended up, not what it is about: it must never own one.
        if (IsBackground(hitObject))
        {
            DyrLog.Info(Tag, "'" + label.name + "' landed on '" + hitObject.name + "', which is "
                + "too big to be an object -> placed there, but not attached to it");
            hitObject = null;
        }
        // Clear of the object, and clear of anything already labelled at that spot.
        int stacked = LabelsStackedAt(point);
        point += Vector3.up * (labelHeightMeters + stacked * labelStackMeters);

        DyrLog.Info(Tag, "Placing '" + label.name + "' at " + point.ToString("F2")
            + (hitObject != null ? " ON '" + hitObject.name + "'"
                : hitReal ? " (on geometry, no object identified)"
                    : " (nothing hit -> guessed depth " + fallbackDistanceMeters
                        + "m; a room scanned with MRUK gives real depth)")
            + (stacked > 0 ? ", stacked above " + stacked + " label(s) already there" : ""));

        if (!persistWithSpatialAnchors)
        {
            SpawnWorldLabel(label, point, hitObject);
            return;
        }

        _claimedPoints[label.entity_id] = point;
        string entityId = label.entity_id;
        ObjectLabelDto captured = label;
        Transform owner = hitObject;
        int generation = _generation;
        DyrMetaBridge.Instance.CreateAnchor(point, (uuid, anchorTransform) =>
        {
            _claimedPoints.Remove(entityId);
            // The source changed while the anchor was being created: another world's label.
            if (generation != _generation) return;

            // The OBJECT wins wherever there is one. Parented to a spatial anchor instead, a label
            // holds a fixed point in the room and is left behind as soon as its object moves —
            // the anchor is still created and still saved below, so the label returns next session.
            bool anchored = !string.IsNullOrEmpty(uuid);
            Transform parent = owner != null ? owner
                : (anchored ? anchorTransform : null);
            SpawnWorldLabel(captured, point, parent);

            if (client == null) return;
            // Persist the anchor association for future sessions.
            if (anchored)
                StartCoroutine(client.BindAnchor(entityId, uuid, "spatial",
                    DyrMetaBridge.CurrentRoomId(), null));
            else if (owner != null)
                StartCoroutine(client.BindAnchor(entityId, ScenePath(owner), "scene",
                    DyrMetaBridge.CurrentRoomId(), null));
        });
    }

    /// <summary>How many world labels stand at this spot, judged by position not by owner.</summary>
    int LabelsStackedAt(Vector3 point)
    {
        int count = 0;
        foreach (DyrLabelView view in _views.Values)
        {
            if (view == null || !view.IsWorldSpace || !view.gameObject.activeSelf) continue;
            if (IsSameSpot(view.transform.position, point)) count++;
        }
        foreach (Vector3 claimed in _claimedPoints.Values)
            if (IsSameSpot(claimed, point)) count++;
        return count;
    }

    /// <summary>Same spot ignoring height, so one stack counts as one column however tall.</summary>
    bool IsSameSpot(Vector3 a, Vector3 b)
    {
        Vector3 offset = a - b;
        offset.y = 0f;
        return offset.sqrMagnitude <= labelClearanceMeters * labelClearanceMeters;
    }

    /// <summary>Is this hit scenery rather than an object? Judged by size, not by name.</summary>
    bool IsBackground(Transform hit)
    {
        if (hit == null) return false;
        Collider collider = hit.GetComponent<Collider>();
        if (collider == null) return false;

        Vector3 size = collider.bounds.size;
        int wide = 0;
        if (size.x > backgroundSizeMeters) wide++;
        if (size.y > backgroundSizeMeters) wide++;
        if (size.z > backgroundSizeMeters) wide++;
        return wide >= 2;
    }

    void SpawnWorldLabel(ObjectLabelDto label, Vector3 position, Transform parent)
    {
        DyrLabelView view = DyrLabelView.CreateWorldSpace(position, label.entity_id, ColourFor(label.entity_id));
        if (parent != null) view.transform.SetParent(parent, worldPositionStays: true);
        view.SetContent(label);
        _views[label.entity_id] = view;
    }

    /// <summary>
    /// Move a label whose object has been picked up and put down somewhere else.
    /// </summary>
    /// <remarks>
    /// Scene understanding knows about eighteen categories of furniture and nothing else, so a
    /// mug or a laptop has no anchor to be attached to and no tracking to follow. What every scan
    /// does give is a fresh box for each recognised object: cast through it and the object's
    /// current position falls out. Only a move past <see cref="moveThresholdMeters"/> counts,
    /// because two casts at the same object never land on exactly the same millimetre and a label
    /// that twitches every scan is worse than one that is slightly stale.
    /// </remarks>
    void FollowIfMoved(ObjectLabelDto label, DyrCaptureView view, DyrLabelView existing)
    {
        if (!followMovedObjects || label.box == null || !label.box.IsValid) return;
        if (!view.valid) view = DyrCaptureView.From(DyrCameras.Head);
        if (!view.valid) return;

        Vector3 point;
        bool hitReal;
        Transform owner = ResolveObject(label.box, view, out point, out hitReal);

        // A ray that hit nothing lands at a guessed depth: that is not evidence of a move.
        if (!hitReal) return;

        float moved = Vector3.Distance(existing.transform.position, point);
        if (moved < moveThresholdMeters) return;

        DyrLog.Info(Tag, "'" + label.name + "' has moved " + moved.ToString("F2")
            + " m -> its label follows");

        existing.transform.SetParent(IsBackground(owner) ? null : owner, worldPositionStays: false);
        existing.transform.position = point;
    }

    /// <summary>Sample points inside the detection box, in normalised box coordinates.</summary>
    /// <remarks>
    /// The centre first, so it wins ties and is the point a label stands on when every sample
    /// agrees. Then two rings: a tight one that is still on the object when the box is loose, and
    /// a wide one that reaches an object filling its box. Rays are geometry — thirteen of them
    /// cost microseconds against the seconds a scan already spends waiting for the model — and
    /// the votes they add are what stops a label landing on the wall behind a chair.
    /// </remarks>
    static readonly Vector2[] BoxSamples =
    {
        new Vector2(0.50f, 0.50f),
        new Vector2(0.35f, 0.40f), new Vector2(0.65f, 0.40f),
        new Vector2(0.35f, 0.60f), new Vector2(0.65f, 0.60f),
        new Vector2(0.50f, 0.30f), new Vector2(0.50f, 0.70f),
        new Vector2(0.30f, 0.50f), new Vector2(0.70f, 0.50f),
        new Vector2(0.20f, 0.35f), new Vector2(0.80f, 0.35f),
        new Vector2(0.20f, 0.65f), new Vector2(0.80f, 0.65f),
    };

    readonly Transform[] _sampleHits = new Transform[BoxSamples.Length];
    readonly Vector3[] _samplePoints = new Vector3[BoxSamples.Length];

    /// <summary>Which object the detection is actually on: the one most of its box hits.</summary>
    Transform ResolveObject(BoxDto box, DyrCaptureView view, out Vector3 point, out bool hitReal)
    {
        point = Vector3.zero;
        hitReal = false;

        for (int i = 0; i < BoxSamples.Length; i++)
        {
            // Box coords are top-down (image convention), the viewport is bottom-up.
            Vector2 viewport = new Vector2(
                box.x0 + BoxSamples[i].x * box.Width,
                1f - (box.y0 + BoxSamples[i].y * box.Height));

            bool sampleHit;
            Transform sampleObject;
            _samplePoints[i] = DyrMetaBridge.ProjectToWorld(
                view.ViewportPointToRay(viewport), fallbackDistanceMeters,
                out sampleHit, out sampleObject);
            _sampleHits[i] = sampleHit ? sampleObject : null;
            if (i == 0) { point = _samplePoints[0]; hitReal = sampleHit; }
        }

        Transform winner = null;
        int bestVotes = 0;
        for (int i = 0; i < _sampleHits.Length; i++)
        {
            if (_sampleHits[i] == null) continue;
            int votes = 0;
            for (int j = 0; j < _sampleHits.Length; j++)
                if (_sampleHits[j] == _sampleHits[i]) votes++;
            // Strictly greater keeps the earliest sample on a tie, and the centre is sampled first.
            if (votes > bestVotes) { winner = _sampleHits[i]; bestVotes = votes; }
        }
        if (winner == null) return null;

        // Stand the label on the winning object, at the most central sample that landed on it.
        for (int i = 0; i < _sampleHits.Length; i++)
        {
            if (_sampleHits[i] != winner) continue;
            point = _samplePoints[i];
            hitReal = true;
            break;
        }
        return winner;
    }

    /// <summary>A stable id for a scene object: its path in the hierarchy.</summary>
    static string ScenePath(Transform target)
    {
        string path = target.name;
        for (Transform parent = target.parent; parent != null; parent = parent.parent)
            path = parent.name + "/" + path;
        return path;
    }

    /// <summary>Updates world-label visibility from view geometry.</summary>
    void LateUpdate()
    {
        UpdateScreenLabels();
        SpreadScreenLabels();

        Camera cam = DyrCameras.Head;
        if (cam == null) return;

        // In front of the wearer, within reach, nearest first: twenty labels at once is a wall of text.
        _visibleCandidates.Clear();
        foreach (DyrLabelView view in _views.Values)
        {
            if (view == null || !view.IsWorldSpace) continue;
            if (!view.IsInView(cam, maxVisibleDistanceMeters))
            {
                if (view.gameObject.activeSelf) view.SetVisible(false);
                continue;
            }
            _visibleCandidates.Add(view);
        }

        if (maxVisibleLabels > 0 && _visibleCandidates.Count > maxVisibleLabels)
        {
            Vector3 eye = cam.transform.position;
            _visibleCandidates.Sort((a, b) =>
                (a.transform.position - eye).sqrMagnitude.CompareTo(
                    (b.transform.position - eye).sqrMagnitude));
        }

        for (int i = 0; i < _visibleCandidates.Count; i++)
        {
            bool visible = maxVisibleLabels <= 0 || i < maxVisibleLabels;
            DyrLabelView view = _visibleCandidates[i];
            if (view.gameObject.activeSelf != visible) view.SetVisible(visible);
        }
    }


    /// <summary>Bring back the labels of objects anchored in a previous session.</summary>
    public void RestoreAnchored(ObjectLabelDto[] known)
    {
        if (known == null || known.Length == 0) return;

        var byUuid = new Dictionary<string, ObjectLabelDto>();
        var uuids = new List<string>();
        foreach (ObjectLabelDto label in known)
        {
            if (!label.HasAnchor || label.anchor_kind != "spatial") continue;
            byUuid[label.anchor_uuid] = label;
            uuids.Add(label.anchor_uuid);
        }
        if (uuids.Count == 0)
        {
            DyrLog.Info(Tag, known.Length + " object(s) known, none anchored yet -> their labels appear "
                + "as soon as the model recognises them in a frame.");
            return;
        }

        DyrMetaBridge.Instance.LoadAnchors(uuids, restored =>
        {
            foreach (KeyValuePair<string, Transform> item in restored)
            {
                ObjectLabelDto label;
                if (!byUuid.TryGetValue(item.Key, out label)) continue;
                if (_views.ContainsKey(label.entity_id)) continue;
                SpawnWorldLabel(label, item.Value.position, item.Value);
                DyrLog.Info(Tag, "Label restored from its anchor: '" + label.name
                    + "' (" + label.entity_id + ")");
            }
        });
    }

    /// <summary>A colour per object, stable for its whole life and as far as possible from its neighbours'.</summary>
    static Color ColourFor(string entityId)
    {
        unchecked
        {
            uint hash = 2166136261u;                    // FNV-1a
            foreach (char c in entityId) { hash ^= c; hash *= 16777619u; }
            float hue = (hash % 1000u) * 0.6180339887f % 1f;
            return Color.HSVToRGB(hue, 0.68f, 0.92f);
        }
    }
}
