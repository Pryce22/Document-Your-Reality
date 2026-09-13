using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>One AR label.</summary>
public class DyrLabelView : MonoBehaviour
{
    const float WorldCanvasScale = 0.0012f;   // 1 UI px -> 1.2 mm
    const float WorldWidthPx = 380f;
    const float BoxThickness = 3f;            // px of detection outline
    // A caption, not a banner.
    const float MaxPanelWidthPx = 460f;
    const float FollowSpeed = 8f;             // higher = snappier
    const float FadeSeconds = 0.6f;           // how long a screen label takes to go out

    RectTransform _panel;
    TMP_Text _titleText;
    TMP_Text _bodyText;
    RectTransform _outline;
    bool _worldSpace;
    Transform _camera;
    Rect _targetBox;
    Rect _currentBox;
    bool _boxInitialised;
    CanvasGroup _group;
    /// <summary>When this screen label fades out. It is measured on a frame the clip has left behind.</summary>
    float _hideAt;

    public string EntityId { get; private set; }
    /// <summary>Where the object is in the picture, normalised and top-down.</summary>
    public Rect NormalizedBox { get; set; }
    /// <summary>World labels hide by geometry; screen labels time themselves out.</summary>
    public bool IsWorldSpace { get { return _worldSpace; } }


    /// <summary>Label drawn over a 2D frame, on the given overlay canvas.</summary>
    public static DyrLabelView CreateScreenSpace(Transform canvasParent, string entityId,
        Color colour, bool showBox = false)
    {
        // RectTransform from the start: a plain Transform under a Canvas makes every child's layout silently wrong.
        GameObject root = new GameObject("Label_" + entityId, typeof(RectTransform));
        root.transform.SetParent(canvasParent, false);

        // STRETCHED OVER THE WHOLE CANVAS, and this is not cosmetic.
        RectTransform rootRect = (RectTransform)root.transform;
        rootRect.anchorMin = Vector2.zero;
        rootRect.anchorMax = Vector2.one;
        rootRect.offsetMin = Vector2.zero;
        rootRect.offsetMax = Vector2.zero;
        rootRect.pivot = Vector2.zero;
        DyrLabelView view = root.AddComponent<DyrLabelView>();
        view._worldSpace = false;
        view.EntityId = entityId;
        // Omit overlapping detection rectangles from full-frame video.
        view.Build(root.transform, colour, showOutline: showBox);
        return view;
    }

    /// <summary>Label floating in the world at a point on the physical object.</summary>
    public static DyrLabelView CreateWorldSpace(Vector3 position, string entityId, Color colour)
    {
        // A root object, so it is kept out of anything the editor could serialise into the scene.
        GameObject root = new GameObject("Label_" + entityId);
        root.hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild;
        root.transform.position = position;
        Canvas canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        RectTransform canvasRect = canvas.GetComponent<RectTransform>();
        canvasRect.sizeDelta = new Vector2(WorldWidthPx, 200f);
        canvasRect.localScale = Vector3.one * WorldCanvasScale;

        DyrLabelView view = root.AddComponent<DyrLabelView>();
        view._worldSpace = true;
        view.EntityId = entityId;
        view.Build(root.transform, colour, showOutline: false);
        return view;
    }

    void Build(Transform parent, Color colour, bool showOutline)
    {
        // Screen labels fade rather than vanish: a label that pops off reads as a glitch.
        if (!_worldSpace) _group = parent.gameObject.AddComponent<CanvasGroup>();

        if (showOutline)
        {
            GameObject outline = new GameObject("Box", typeof(RectTransform));
            outline.transform.SetParent(parent, false);
            _outline = (RectTransform)outline.transform;
            _outline.pivot = Vector2.zero;
            _outline.anchorMin = _outline.anchorMax = Vector2.zero;

            // An OUTLINE, not a translucent fill.
            AddEdge(_outline, "Top", new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, BoxThickness), colour);
            AddEdge(_outline, "Bottom", new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(0f, BoxThickness), colour);
            AddEdge(_outline, "Left", new Vector2(0f, 0f), new Vector2(0f, 1f),
                new Vector2(BoxThickness, 0f), colour);
            AddEdge(_outline, "Right", new Vector2(1f, 0f), new Vector2(1f, 1f),
                new Vector2(BoxThickness, 0f), colour);
        }

        GameObject panel = new GameObject("Panel", typeof(RectTransform));
        panel.transform.SetParent(parent, false);
        _panel = (RectTransform)panel.transform;
        panel.AddComponent<Image>().color = new Color(colour.r, colour.g, colour.b, 0.88f);
        _panel.pivot = Vector2.zero;
        if (!_worldSpace)
        {
            _panel.anchorMin = _panel.anchorMax = Vector2.zero;
        }
        else
        {
            _panel.anchorMin = _panel.anchorMax = new Vector2(0.5f, 0.5f);
            _panel.pivot = new Vector2(0.5f, 0f);
            _panel.sizeDelta = new Vector2(WorldWidthPx, 160f);
            _panel.anchoredPosition = Vector2.zero;
        }

        VerticalLayoutGroup layout = panel.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(10, 10, 8, 8);
        layout.spacing = 2;
        layout.childForceExpandHeight = false;
        layout.childControlHeight = true;
        layout.childControlWidth = true;
        ContentSizeFitter fitter = panel.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        _titleText = MakeText(panel.transform, "Title", 26, FontStyles.Bold);
        _bodyText = MakeText(panel.transform, "Body", 20, FontStyles.Normal);
    }

    static void AddEdge(
        Transform parent, string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 size, Color colour)
    {
        GameObject edge = new GameObject(name, typeof(RectTransform));
        edge.transform.SetParent(parent, false);
        RectTransform rect = (RectTransform)edge.transform;
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = size;
        rect.anchoredPosition = Vector2.zero;
        edge.AddComponent<Image>().color = colour;
    }

    static TMP_Text MakeText(Transform parent, string name, float size, FontStyles style)
    {
        GameObject textObject = new GameObject(name, typeof(RectTransform));
        textObject.transform.SetParent(parent, false);
        TextMeshProUGUI text = textObject.AddComponent<TextMeshProUGUI>();
        text.fontSize = size;
        text.fontStyle = style;
        text.color = Color.white;
        // TMP renamed this in 3.2 (Unity 2023.2 / Unity 6).
#if UNITY_2023_2_OR_NEWER
        text.textWrappingMode = TextWrappingModes.Normal;
#else
        text.enableWordWrapping = true;
#endif
        text.overflowMode = TextOverflowModes.Ellipsis;
        return text;
    }


    public void SetContent(ObjectLabelDto label)
    {
        string title = string.IsNullOrEmpty(label.title) ? label.name : label.title;

        // NEW marks first documentation; SEEN marks re-identification.
        string badge = label.is_new ? "NEW" : "SEEN";
        _titleText.text = "<size=65%>" + badge + "</size>  " + title;

        // Fall back to distilled memory when re-identification has no new body.
        string body = !string.IsNullOrEmpty(label.body) ? label.body : label.memory_summary;
        if (label.steps != null && label.steps.Length > 0)
        {
            for (int i = 0; i < label.steps.Length && i < 3; i++)
                body += "\n" + (i + 1) + ". " + label.steps[i];
        }
        _bodyText.text = body;
    }


    /// <summary>Screen placement: the box the label should follow, in pixels.</summary>
    public void SetScreenTarget(Rect box)
    {
        _targetBox = box;
        if (!_boxInitialised)
        {
            // First sighting: appear where the object is, do not slide in from the corner of the screen.
            _currentBox = box;
            _boxInitialised = true;
            ApplyScreenBox(_currentBox);
        }
    }

    public void SetVisible(bool visible)
    {
        if (gameObject.activeSelf != visible) gameObject.SetActive(visible);
    }

    /// <summary>Up for a few seconds, then out on its own: behind it the clip has moved on.</summary>
    public void ShowFor(float seconds)
    {
        _hideAt = Time.unscaledTime + Mathf.Max(FadeSeconds, seconds);
        if (_group != null) _group.alpha = 1f;
        SetVisible(true);
    }

    void ApplyScreenBox(Rect box)
    {
        if (_outline != null)
        {
            _outline.anchoredPosition = new Vector2(box.x, box.y);
            _outline.sizeDelta = new Vector2(box.width, box.height);
        }

        // XR canvas and screen sizes can differ.
        Vector2 area = DyrFrameUtils.CanvasArea(transform.parent);

        float panelWidth = Mathf.Clamp(box.width, 220f, Mathf.Min(MaxPanelWidthPx, area.x * 0.9f));
        if (!Mathf.Approximately(_panel.sizeDelta.x, panelWidth))
        {
            _panel.sizeDelta = new Vector2(panelWidth, _panel.sizeDelta.y);
            LayoutRebuilder.ForceRebuildLayoutImmediate(_panel);
        }

        PanelHeight = _panel.rect.height;

        // ON the object, not above it.
        float x = Mathf.Clamp(box.center.x - panelWidth * 0.5f, 0f, Mathf.Max(0f, area.x - panelWidth));
        PanelBaseY = Mathf.Clamp(box.center.y - PanelHeight * 0.5f, 0f, Mathf.Max(0f, area.y - PanelHeight));
        float y = Mathf.Clamp(PanelBaseY + PanelOffsetY, 0f, Mathf.Max(0f, area.y - PanelHeight));
        _panel.anchoredPosition = new Vector2(x, y);
    }

    /// <summary>Where the panel would sit with nothing in its way, and how tall it is.</summary>
    public float PanelBaseY { get; private set; }
    public float PanelHeight { get; private set; }
    /// <summary>Vertical nudge applied to keep two labels from covering each other.</summary>
    public float PanelOffsetY { get; private set; }

    public void SetPanelOffset(float offsetY) { PanelOffsetY = offsetY; }

    void LateUpdate()
    {
        if (_worldSpace)
        {
            BillboardToCamera();
            return;
        }

        if (_hideAt > 0f)
        {
            float left = _hideAt - Time.unscaledTime;
            if (left <= 0f) { SetVisible(false); return; }
            if (_group != null) _group.alpha = Mathf.Clamp01(left / FadeSeconds);
        }

        if (!_boxInitialised) return;
        // Ease toward the box the last scan reported.
        float t = 1f - Mathf.Exp(-FollowSpeed * Time.unscaledDeltaTime);
        _currentBox = new Rect(
            Mathf.Lerp(_currentBox.x, _targetBox.x, t),
            Mathf.Lerp(_currentBox.y, _targetBox.y, t),
            Mathf.Lerp(_currentBox.width, _targetBox.width, t),
            Mathf.Lerp(_currentBox.height, _targetBox.height, t));
        ApplyScreenBox(_currentBox);
    }

    void BillboardToCamera()
    {
        if (_camera == null)
        {
            if (DyrCameras.Head == null) return;
            _camera = DyrCameras.Head.transform;
        }
        // Billboard, but upright: a label that rolls with the head is unreadable.
        Vector3 away = transform.position - _camera.position;
        away.y = 0f;
        if (away.sqrMagnitude > 0.0001f) transform.rotation = Quaternion.LookRotation(away);
    }

    /// <summary>Is this world label in front of the wearer?</summary>
    public bool IsInView(Camera camera, float maxDistanceMeters)
    {
        if (camera == null) return true;
        Vector3 toLabel = transform.position - camera.transform.position;
        if (toLabel.magnitude > maxDistanceMeters) return false;
        Vector3 viewport = camera.WorldToViewportPoint(transform.position);
        return viewport.z > 0f
            && viewport.x > -0.05f && viewport.x < 1.05f
            && viewport.y > -0.05f && viewport.y < 1.05f;
    }
}
