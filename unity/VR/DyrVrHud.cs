using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>The status panel, inside the headset.</summary>
public class DyrVrHud : MonoBehaviour
{
    const string Tag = "vr-hud";

    [Tooltip("Metres in front of the eyes.")]
    public float distanceMeters = 1.1f;
    [Tooltip("Metres below the line of sight, so it does not cover what you are documenting.")]
    public float dropMeters = 0.38f;
    public float widthMeters = 0.62f;
    [Tooltip("Higher = the panel keeps up with the head more closely. Low values feel calmer.")]
    public float followSpeed = 5f;
    public int layer = 5;   // UI: kept out of DyrVrSceneFrameSource captures

    DyrScanLoop _loop;
    DyrBackendClient _client;
    DyrLabelPlacer _placer;
    TMP_Text _headline;
    TMP_Text _status;
    TMP_Text _hint;
    Transform _panel;
    float _nextRefresh;

    /// <summary>Add the HUD to a host object unless one is already there.</summary>
    public static DyrVrHud Ensure(GameObject host)
    {
        DyrVrHud existing = FindAnyObjectByType<DyrVrHud>();
        return existing != null ? existing : host.AddComponent<DyrVrHud>();
    }

    void Start()
    {
        _loop = FindAnyObjectByType<DyrScanLoop>();
        _client = FindAnyObjectByType<DyrBackendClient>();
        _placer = FindAnyObjectByType<DyrLabelPlacer>();
        Build();
        DyrLog.Info(Tag, "In-headset status panel ready");
    }

    void Build()
    {
        GameObject host = new GameObject("DyrVrHud");
        host.hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild;
        host.layer = layer;
        Canvas canvas = host.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;

        RectTransform rect = (RectTransform)host.transform;
        rect.sizeDelta = new Vector2(620f, 200f);
        rect.localScale = Vector3.one * (widthMeters / rect.sizeDelta.x);
        _panel = host.transform;

        GameObject background = new GameObject("Background", typeof(RectTransform));
        background.transform.SetParent(host.transform, false);
        background.layer = layer;
        background.AddComponent<Image>().color = new Color(0.05f, 0.06f, 0.09f, 0.82f);
        RectTransform backgroundRect = (RectTransform)background.transform;
        backgroundRect.anchorMin = Vector2.zero;
        backgroundRect.anchorMax = Vector2.one;
        backgroundRect.offsetMin = Vector2.zero;
        backgroundRect.offsetMax = Vector2.zero;

        VerticalLayoutGroup layout = background.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(18, 18, 14, 14);
        layout.spacing = 4;
        layout.childControlHeight = true;
        layout.childControlWidth = true;
        layout.childForceExpandHeight = false;

        _headline = MakeText(background.transform, "Headline", 30, FontStyles.Bold, Color.white);
        _status = MakeText(background.transform, "Status", 26, FontStyles.Normal, new Color(0.85f, 0.92f, 1f));
        _hint = MakeText(background.transform, "Hint", 22, FontStyles.Normal, new Color(0.65f, 0.70f, 0.78f));
    }

    TMP_Text MakeText(Transform parent, string name, float size, FontStyles style, Color colour)
    {
        GameObject textObject = new GameObject(name, typeof(RectTransform));
        textObject.transform.SetParent(parent, false);
        textObject.layer = layer;
        TextMeshProUGUI text = textObject.AddComponent<TextMeshProUGUI>();
        text.fontSize = size;
        text.fontStyle = style;
        text.color = colour;
#if UNITY_2023_2_OR_NEWER
        text.textWrappingMode = TextWrappingModes.Normal;
#else
        text.enableWordWrapping = true;
#endif
        text.overflowMode = TextOverflowModes.Ellipsis;
        return text;
    }

    void LateUpdate()
    {
        Camera cam = DyrCameras.Head;
        if (cam == null || _panel == null) return;

        Vector3 target = cam.transform.position
            + cam.transform.forward * distanceMeters
            - cam.transform.up * dropMeters;
        // Forward away from the eyes: that is the readable side of a world-space canvas.
        Quaternion facing = Quaternion.LookRotation(target - cam.transform.position, Vector3.up);
        float t = 1f - Mathf.Exp(-followSpeed * Time.unscaledDeltaTime);
        _panel.SetPositionAndRotation(
            Vector3.Lerp(_panel.position, target, t),
            Quaternion.Slerp(_panel.rotation, facing, t));

        // The panel has to MOVE every frame or it swims, but its text changes a few times a minute.
        if (Time.unscaledTime < _nextRefresh) return;
        _nextRefresh = Time.unscaledTime + 0.2f;
        Refresh();
    }

    void Refresh()
    {
        if (_loop == null)
        {
            _headline.text = "DYR";
            _status.text = "no DyrScanLoop in the scene";
            return;
        }
        int labels = _placer != null ? _placer.VisibleLabelCount : 0;
        _headline.text = "DYR " + _loop.ActiveMode + "   ·   " + labels + " label"
            + (labels == 1 ? "" : "s");

        if (showLog)
        {
            // Scoped storage hides the log file from the headset's Files app, so the last lines
            // are shown here instead: on-device, this is the only way to read them.
            string[] lines = DyrLog.RecentLines();
            int from = Mathf.Max(0, lines.Length - LogLinesShown);
            _status.text = string.Join("\n", lines, from, lines.Length - from);
            _hint.text = "Y / L / ring-pinch = hide the log";
            ApplyLogLayout(true);
            return;
        }
        ApplyLogLayout(false);

        _status.text = _loop.Status;
        _hint.text = (_client != null ? _client.LlmStatusLine : "no backend client")
            + "\nA / Space / right middle-pinch = scan"
            + "     B / N / left middle-pinch = auto scan"
            + "\nY / L / ring-pinch = show the log";

        // An exception would otherwise take the session down with nothing on screen to say so.
        if (!string.IsNullOrEmpty(DyrLog.LastCrash))
        {
            _status.color = new Color(1f, 0.55f, 0.5f);
            _status.text = "CRASH: " + DyrLog.LastCrash;
        }
    }

    /// <summary>Show the recent log instead of the status lines.</summary>
    public bool showLog;

    /// <summary>Lines of log the panel can hold before the text is clipped.</summary>
    const int LogLinesShown = 14;

    bool _logLayoutApplied;

    /// <summary>
    /// Grow the panel and shrink its text for the log, then put both back.
    /// </summary>
    /// <remarks>
    /// Log lines are far longer than a status line: at the status size they run off the panel,
    /// which cannot be resized from inside a headset.
    /// </remarks>
    void ApplyLogLayout(bool forLog)
    {
        if (_logLayoutApplied == forLog || _panel == null) return;
        _logLayoutApplied = forLog;

        RectTransform rect = (RectTransform)_panel;
        rect.sizeDelta = forLog ? new Vector2(1100f, 620f) : new Vector2(620f, 200f);
        float width = forLog ? widthMeters * 2.1f : widthMeters;
        rect.localScale = Vector3.one * (width / rect.sizeDelta.x);

        if (_status != null) _status.fontSize = forLog ? 17f : 26f;
        if (_headline != null) _headline.fontSize = forLog ? 24f : 30f;
        if (_hint != null) _hint.fontSize = forLog ? 17f : 22f;
    }

    void Update()
    {
        if (DyrInput.ToggleLogPressed())
        {
            showLog = !showLog;
            DyrLog.Info(Tag, "Log view " + (showLog ? "ON" : "OFF"));
        }
    }
}
