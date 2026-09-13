using System.Collections;
using UnityEngine;
#if DYR_META_SDK
using Meta.XR;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif
#endif

/// <summary>The headset's passthrough camera (Meta Passthrough Camera API).</summary>
public class DyrPassthroughFrameSource : MonoBehaviour, IDyrFrameSource,
    IDyrFrameViewSource, IDyrCapturedRayProvider
{
    const string Tag = "passthrough-cam";
#if DYR_META_SDK && UNITY_ANDROID && !UNITY_EDITOR
    const string HeadsetCameraPermission = "horizonos.permission.HEADSET_CAMERA";
#endif

    /// <summary>The permission a scan needs, named for startup diagnostics.</summary>
    public const string HeadsetCameraPermissionName = "horizonos.permission.HEADSET_CAMERA";

    /// <summary>
    /// Whether the headset camera may be read right now.
    /// </summary>
    /// <remarks>
    /// Denied, every scan fails on an empty frame and nothing on screen says why, so startup
    /// reports it. Always true off-device, where there is no permission to grant.
    /// </remarks>
    public static bool HasCameraPermission
    {
        get
        {
#if DYR_META_SDK && UNITY_ANDROID && !UNITY_EDITOR
            // Asked before the Android side is up, this throws; a diagnostic must not be the
            // thing that ends the session.
            try { return Permission.HasUserAuthorizedPermission(HeadsetCameraPermission); }
            catch (System.Exception) { return false; }
#else
            return true;
#endif
        }
    }

    [Tooltip("Use the right RGB camera instead of the left one.")]
    public bool useRightCamera;
    public int requestedWidth = 1280;
    public int requestedHeight = 960;
    public int requestedFps = 30;
    [Range(1, 100)] public int jpegQuality = 85;

#if DYR_META_SDK
    PassthroughCameraAccess _camera;
    Pose _capturedPose;
    bool _hasCapturedPose;
    Texture2D _readback;
#endif

    public bool IsReady
    {
        get
        {
#if DYR_META_SDK
            // IsSupported is static in MRUK v203: it answers for the headset, not for one camera.
            return _camera != null && PassthroughCameraAccess.IsSupported && _camera.IsPlaying
                && _camera.GetTexture() != null;
#else
            return false;
#endif
        }
    }

    public string SourceName { get { return "headset"; } }

    public DyrCaptureView CaptureView
    {
        get
        {
            DyrCaptureView view = new DyrCaptureView();
#if DYR_META_SDK
            if (!_hasCapturedPose) return view;
            Vector2Int resolution = _camera.CurrentResolution;
            PassthroughCameraAccess.CameraIntrinsics intrinsics = _camera.Intrinsics;
            view.position = _capturedPose.position;
            view.rotation = _capturedPose.rotation;
            view.aspect = resolution.y > 0 ? (float)resolution.x / resolution.y : 4f / 3f;
            view.verticalFov = intrinsics.FocalLength.y > 0f
                ? 2f * Mathf.Atan(intrinsics.SensorResolution.y
                    / (2f * intrinsics.FocalLength.y)) * Mathf.Rad2Deg
                : 60f;
            view.valid = true;
            view.rayProvider = this;
#endif
            return view;
        }
    }

    void Start()
    {
#if !DYR_META_SDK
        DyrLog.Error(Tag, "Passthrough mode requires Meta MRUK and DYR_META_SDK. "
            + "Video and Vr modes remain available without them.");
#elif UNITY_ANDROID && !UNITY_EDITOR
        if (!Permission.HasUserAuthorizedPermission(HeadsetCameraPermission))
        {
            DyrLog.Info(Tag, "Requesting permission " + HeadsetCameraPermission + " ...");
            var callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += _ =>
            {
                DyrLog.Info(Tag, "Camera permission GRANTED");
                StartCamera();
            };
            callbacks.PermissionDenied += _ => DyrLog.Error(Tag, "Camera permission DENIED - every scan will fail. "
                + "Grant it in the headset settings or reinstall the app.");
            Permission.RequestUserPermission(HeadsetCameraPermission, callbacks);
            return;
        }
        StartCamera();
#else
        StartCamera();
#endif
    }

    void StartCamera()
    {
#if DYR_META_SDK
        _camera = GetComponent<PassthroughCameraAccess>();
        if (_camera == null) _camera = gameObject.AddComponent<PassthroughCameraAccess>();
        _camera.enabled = false;
        _camera.CameraPosition = useRightCamera
            ? PassthroughCameraAccess.CameraPositionType.Right
            : PassthroughCameraAccess.CameraPositionType.Left;
        _camera.RequestedResolution = new Vector2Int(requestedWidth, requestedHeight);
        _camera.MaxFramerate = Mathf.Clamp(requestedFps, 1, 60);
        _camera.enabled = true;
        StartCoroutine(LogWhenReady());
#endif
    }

    /// <summary>
    /// Keep reopening the camera until it plays, instead of giving up after one attempt.
    /// </summary>
    /// <remarks>
    /// The permission dialog is answered seconds after Start, and a camera opened before the
    /// grant never recovers on its own. One retry is not enough either: the wearer may take a
    /// while to find the button, and a headset that has just woken can refuse the first reopen.
    /// Each attempt also alternates the eye, because one camera can be held by another app.
    /// </remarks>
    IEnumerator LogWhenReady()
    {
        float t0 = Time.realtimeSinceStartup;
        while (!IsReady && Time.realtimeSinceStartup - t0 < 5f) yield return null;

#if DYR_META_SDK
        for (int attempt = 1; !IsReady && attempt <= CameraRetryAttempts; attempt++)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!Permission.HasUserAuthorizedPermission(HeadsetCameraPermission))
            {
                // Nothing to retry until the wearer answers: wait, do not burn attempts.
                DyrLog.Info(Tag, "Waiting for the camera permission before retrying ...");
                float waitStart = Time.realtimeSinceStartup;
                while (!Permission.HasUserAuthorizedPermission(HeadsetCameraPermission)
                    && Time.realtimeSinceStartup - waitStart < PermissionWaitSeconds)
                    yield return new WaitForSecondsRealtime(0.5f);

                if (!Permission.HasUserAuthorizedPermission(HeadsetCameraPermission))
                {
                    DyrLog.Error(Tag, "The camera permission was never granted: no scan can run. "
                        + "Grant it in Settings > Apps > Document Your Reality > Permissions.");
                    yield break;
                }
                attempt = 1;   // The permission just arrived: this is a fresh start, not a retry.
            }
#endif
            // Alternate eyes: a camera held by another app never plays, the other one does.
            useRightCamera = !useRightCamera;
            DyrLog.Info(Tag, "Camera not ready (" + WhyNotReady() + ") -> reopening it, attempt "
                + attempt + " of " + CameraRetryAttempts + " on the "
                + (useRightCamera ? "right" : "left") + " eye");

            if (_camera != null)
            {
                _camera.enabled = false;
                yield return null;
                _camera.CameraPosition = useRightCamera
                    ? PassthroughCameraAccess.CameraPositionType.Right
                    : PassthroughCameraAccess.CameraPositionType.Left;
                _camera.enabled = true;
            }
            else
            {
                StartCamera();
                yield break;   // StartCamera starts this coroutine again.
            }

            float retryStart = Time.realtimeSinceStartup;
            while (!IsReady && Time.realtimeSinceStartup - retryStart < CameraRetrySeconds)
                yield return null;
        }

        if (IsReady)
        {
            Vector2Int resolution = _camera.CurrentResolution;
            DyrLog.Info(Tag, "PassthroughCameraAccess READY in "
                + (Time.realtimeSinceStartup - t0).ToString("F1") + "s -> "
                + resolution.x + "x" + resolution.y + " @" + _camera.MaxFramerate + " fps, "
                + _camera.CameraPosition + " camera");
        }
        else
            DyrLog.Error(Tag, "PassthroughCameraAccess never started after " + CameraRetryAttempts
                + " attempts -> " + WhyNotReady() + ". Check Quest 3/3S support, headset v74+, "
                + "and the HEADSET_CAMERA permission.");
#endif
    }

#if DYR_META_SDK
    /// <summary>
    /// Encode the camera's own texture when the colour buffer is not usable.
    /// </summary>
    /// <remarks>
    /// GetColors() and GetTexture() do not fill at the same moment, and the buffer is also sized
    /// for whatever resolution the camera last settled on. Blitting the texture through a
    /// RenderTexture reads whatever is actually on screen, whichever kind of texture it is.
    /// </remarks>
    byte[] EncodeFromTexture()
    {
        Texture source = _camera.GetTexture();
        if (source == null || source.width == 0 || source.height == 0) return null;

        RenderTexture previous = RenderTexture.active;
        RenderTexture staging = RenderTexture.GetTemporary(
            source.width, source.height, 0, RenderTextureFormat.ARGB32);
        try
        {
            Graphics.Blit(source, staging);
            RenderTexture.active = staging;

            if (_readback == null || _readback.width != source.width || _readback.height != source.height)
                _readback = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);

            _readback.ReadPixels(new Rect(0f, 0f, source.width, source.height), 0, 0, false);
            _readback.Apply(false);
            return _readback.EncodeToJPG(jpegQuality);
        }
        catch (System.Exception error)
        {
            DyrLog.Warn(Tag, "Reading the camera texture failed (" + error.Message + ")");
            return null;
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(staging);
        }
    }
#endif

    /// <summary>
    /// Reopen the camera after a failed capture, unless a recovery is already running.
    /// </summary>
    /// <remarks>
    /// A camera lost mid-session (the headset slept, another app took it) never comes back on
    /// its own, so every later scan fails identically until something reopens it.
    /// </remarks>
    public void Revive()
    {
#if DYR_META_SDK
        if (IsReady || _reviving) return;
        _reviving = true;
        StartCoroutine(ReviveRoutine());
#endif
    }

#if DYR_META_SDK
    bool _reviving;

    IEnumerator ReviveRoutine()
    {
        DyrLog.Info(Tag, "Capture failed -> trying to bring the camera back");
        yield return LogWhenReady();
        _reviving = false;
    }
#endif

    /// <summary>Reopen attempts before giving up; each alternates the eye.</summary>
    const int CameraRetryAttempts = 6;

    /// <summary>Seconds an attempt is given to produce a texture.</summary>
    const float CameraRetrySeconds = 3f;

    /// <summary>Seconds to wait for the wearer to answer the permission dialog.</summary>
    const float PermissionWaitSeconds = 60f;

    /// <summary>
    /// Which of IsReady's conditions is the one failing.
    /// </summary>
    /// <remarks>
    /// "Not ready" alone sends you round every possible cause; each of these points at one.
    /// </remarks>
    public string WhyNotReady()
    {
#if DYR_META_SDK
        if (_camera == null) return "no PassthroughCameraAccess component (StartCamera never ran, "
            + "which means the camera permission was refused or is still pending)";
        if (!PassthroughCameraAccess.IsSupported) return "PassthroughCameraAccess.IsSupported is "
            + "false (Quest 3/3S only, and the headset must be on v74+)";
        if (!_camera.IsPlaying) return "the camera component exists but is not playing "
            + "(permission granted too late, or another app holds the camera)";
        if (_camera.GetTexture() == null) return "the camera is playing but has produced no "
            + "texture yet (give it another second or two)";
        return "every condition passes - it became ready just now";
#else
        return "built without DYR_META_SDK";
#endif
    }

    public byte[] CaptureJpeg()
    {
#if DYR_META_SDK
        if (!IsReady)
        {
            DyrLog.Warn(Tag, "Capture refused: passthrough camera not ready");
            return null;
        }

        _capturedPose = _camera.GetCameraPose();
        _hasCapturedPose = true;

        Vector2Int resolution = _camera.CurrentResolution;
        var colors = _camera.GetColors();

        // The colour buffer lags the texture: it can be empty, or sized for the resolution the
        // camera had a moment ago, while GetTexture() already has the frame. Reading the texture
        // instead of giving up is the difference between a scan and "capture failed".
        if (colors.IsCreated && colors.Length == resolution.x * resolution.y)
        {
            if (_readback == null || _readback.width != resolution.x || _readback.height != resolution.y)
                _readback = new Texture2D(resolution.x, resolution.y, TextureFormat.RGBA32, false);
            _readback.SetPixelData(colors, 0);
            _readback.Apply(false);
            return _readback.EncodeToJPG(jpegQuality);
        }

        byte[] fromTexture = EncodeFromTexture();
        if (fromTexture != null) return fromTexture;

        DyrLog.Warn(Tag, "Capture found no pixels: the colour buffer holds "
            + (colors.IsCreated ? colors.Length.ToString() : "nothing")
            + " for a " + resolution.x + "x" + resolution.y + " frame, and the texture could not "
            + "be read either");
        return null;
#else
        return null;
#endif
    }

    public float FocusScore()
    {
#if DYR_META_SDK
        return IsReady ? DyrFrameUtils.FocusScore(_camera.GetTexture()) : 0f;
#else
        return 0f;
#endif
    }

    public bool TryGetCaptureRay(Vector2 viewport, out Ray ray)
    {
#if DYR_META_SDK
        if (_camera != null && _hasCapturedPose)
        {
            ray = _camera.ViewportPointToRay(viewport, _capturedPose);
            return true;
        }
#endif
        ray = new Ray();
        return false;
    }

    /// <summary>Passthrough is the real world: labels go in the world, not on a rect.</summary>
    public bool TryGetDisplayRect(out Rect rect) { rect = new Rect(); return false; }

    void OnDestroy()
    {
#if DYR_META_SDK
        if (_camera != null) _camera.enabled = false;
#endif
    }
}
