using UnityEngine;

/// <summary>Captures the virtual room from the wearer's view.</summary>
public class DyrVrSceneFrameSource : MonoBehaviour, IDyrFrameSource, IDyrFrameViewSource
{
    const string Tag = "vr-scene";

    [Tooltip("Width of the frame sent to the model. 896 matches the backend's max_image_px.")]
    public int captureWidth = 896;
    [Tooltip("Vertical FOV of the capture camera. Not the headset's 121: a fisheye grounds badly.")]
    public float captureVerticalFov = 60f;
    [Tooltip("Aspect of the captured frame. 4:3 like the headset's passthrough camera.")]
    public float captureAspect = 4f / 3f;
    [Range(1, 100)] public int jpegQuality = 85;
    [Tooltip("Seconds between renders of the capture camera. This is a second full render. 0 = every frame.")]
    public float renderIntervalSeconds = 0.2f;
    [Tooltip("Layer kept out of the captured frame, so the model never sees its own labels.")]
    public int labelLayer = 5;      // built-in UI layer

    Camera _capture;
    RenderTexture _target;
    Texture2D _readback;
    int _framesRendered;
    float _nextRelayer;
    float _nextRender;

    public bool IsReady { get { return _target != null && _framesRendered > 1 && Camera.main != null; } }
    public string SourceName { get { return "vr-room"; } }

    /// <summary>View used for the latest rendered frame.</summary>
    public DyrCaptureView CaptureView { get; private set; }

    void Start()
    {
        GameObject host = new GameObject("DyrVrCaptureCamera");
        host.transform.SetParent(transform, false);
        _capture = host.AddComponent<Camera>();
        _capture.stereoTargetEye = StereoTargetEyeMask.None;   // mono, never a stereo pass
        _capture.cullingMask = ~(1 << labelLayer);
        _capture.depth = -100;
        DyrLog.Info(Tag, "Capture camera created (labels on layer " + labelLayer + " excluded)");
    }

    void LateUpdate()
    {
        Camera main = Camera.main;
        if (main == null || _capture == null) return;

        ConfigureCapture();
        _capture.transform.SetPositionAndRotation(main.transform.position, main.transform.rotation);

        // Switched on for one frame at a time.
        bool render = Time.unscaledTime >= _nextRender;
        if (_capture.enabled != render) _capture.enabled = render;
        if (render)
        {
            _nextRender = Time.unscaledTime + Mathf.Max(0f, renderIntervalSeconds);
            if (_target != null)
            {
                _framesRendered++;
                // Record the view on the frame rendered by this camera.
                CaptureView = DyrCaptureView.From(_capture);
            }
        }

        // Refresh periodically so new labels leave the next capture.
        if (Time.unscaledTime < _nextRelayer) return;
        _nextRelayer = Time.unscaledTime + 0.5f;
        MoveLabelsOffCamera();
    }

    /// <summary>A fixed, photographic frustum — NOT the eye's.</summary>
    void ConfigureCapture()
    {
        _capture.fieldOfView = Mathf.Clamp(captureVerticalFov, 20f, 120f);
        _capture.nearClipPlane = 0.05f;
        _capture.farClipPlane = 100f;

        float aspect = captureAspect > 0.01f ? captureAspect : 4f / 3f;
        int width = Mathf.Clamp(captureWidth, 240, 2048);
        int height = Mathf.Clamp(Mathf.RoundToInt(width / aspect), 240, 2048);
        if (_target != null && _target.width == width && _target.height == height) return;

        if (_target != null) _target.Release();
        _target = new RenderTexture(width, height, 24);
        _capture.targetTexture = _target;
        _framesRendered = 0;
        DyrLog.Info(Tag, "Capture texture " + width + "x" + height + " (aspect "
            + aspect.ToString("F2") + ", fov " + _capture.fieldOfView.ToString("F0") + " vertical = "
            + (2f * Mathf.Atan(Mathf.Tan(_capture.fieldOfView * 0.5f * Mathf.Deg2Rad) * aspect)
                * Mathf.Rad2Deg).ToString("F0") + " horizontal)");
    }

    void MoveLabelsOffCamera()
    {
        DyrLabelView[] views = FindObjectsByType<DyrLabelView>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (DyrLabelView view in views)
            if (view != null && view.gameObject.layer != labelLayer)
                SetLayerRecursively(view.transform, labelLayer);
    }

    static void SetLayerRecursively(Transform root, int layer)
    {
        root.gameObject.layer = layer;
        for (int i = 0; i < root.childCount; i++) SetLayerRecursively(root.GetChild(i), layer);
    }

    public byte[] CaptureJpeg()
    {
        if (!IsReady)
        {
            DyrLog.Warn(Tag, "Capture refused: the capture camera has not rendered a frame yet");
            return null;
        }
        return DyrFrameUtils.EncodeRenderTexture(_target, ref _readback, jpegQuality);
    }

    /// <summary>A rendered frame is never blurred, so this is constant. It must NOT force a render.</summary>
    public float FocusScore() { return IsReady ? 1f : 0f; }

    /// <summary>The frame IS the world in front of you: labels go in the world.</summary>
    public bool TryGetDisplayRect(out Rect rect) { rect = new Rect(); return false; }

    void OnDestroy()
    {
        if (_target != null) _target.Release();
    }
}
