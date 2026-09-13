using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>The driver: grabs frames, sends them to the backend, hands the labels to the placer.</summary>
public class DyrScanLoop : MonoBehaviour
{
    const string Tag = "loop";

    public enum DyrMode
    {
        /// <summary>Passthrough when a headset offers it, video otherwise.</summary>
        Auto,
        /// <summary>Always the video in StreamingAssets.</summary>
        Video,
        /// <summary>Always the headset passthrough camera.</summary>
        Passthrough,
        /// <summary>VR: a virtual test room instead of the real one.</summary>
        Vr,
    }

    [Header("Source")]
    [SerializeField] private DyrMode mode = DyrMode.Auto;
    [Tooltip("Video mode only: file name inside Assets/StreamingAssets.")]
    [SerializeField] private string videoFileName = "test.mp4";
    [Tooltip("Video mode only: start the clip over when it ends. Off when filming, so the take is one pass.")]
    [SerializeField] private bool loopVideo = true;
    [Tooltip("Video mode: play a <clip>_frames folder of JPEGs instead of the mp4. The escape "
        + "hatch for a machine whose media stack will not decode; the mp4 is the normal path.")]
    [SerializeField] private bool useFrameSequence;

    /// <summary>Clip played in Video mode.</summary>
    public string VideoFileName { get { return videoFileName; } }

    [Header("Backend")]
    [Tooltip("Used when no DyrBackendClient is present: 127.0.0.1 in the Editor, the LAN IP on a headset.")]
    [SerializeField] private string backendBaseUrl = "http://127.0.0.1:8000";

    [Header("Scanning")]
    [Tooltip("Seconds of pause after a scan, on top of the model's own time. Small: the picture "
        + "never stops, so a long pause only buys unlabelled video.")]
    [SerializeField] private float scanPauseSeconds = 2f;
    [Tooltip("Keep scanning on a timer. Off = only manual scans (Space / controller A).")]
    [SerializeField] private bool autoScan = true;
    [Tooltip("Seconds spent looking for a sharp frame before each scan. A blurred frame recognises nothing.")]
    [SerializeField] private float focusWindowSeconds = 0.5f;
    [Header("VR mode only")]
    [Tooltip("Show the status panel inside the headset; the OnGUI box below never reaches one.")]
    [SerializeField] private bool showVrHud = true;

    [Tooltip("Small on-screen status box. Useful on a flat screen, invisible inside a headset.")]
    [SerializeField] private bool showStatusOverlay = true;

    DyrBackendClient _client;
    DyrLabelPlacer _placer;
    IDyrFrameSource _source;
    bool _scanning;
    float _lastScanSeconds;
    float _scanStarted;
    int _scanCount;      // frames analysed
    int _modelCalls;     // scans answered, one model call each
    int _failCount;
    float _scanSecondsTotal;

    /// <summary>Seconds since a moment stamped with Time.realtimeSinceStartup, never negative.</summary>
    static float SecondsSince(float stamp)
    {
        return Mathf.Max(0f, Time.realtimeSinceStartup - stamp);
    }

    /// <summary>Live progress of the scan in flight, for the on-screen bar.</summary>
    public bool IsScanning { get { return _scanning; } }
    public float ScanSeconds { get { return _scanning ? SecondsSince(_scanStarted) : 0f; } }
    public int ScanLabelCount { get; private set; }
    public string ScanPhase { get; private set; } = "";

    /// <summary>Mean model answer time so far, for the on-screen readout.</summary>
    public float AverageScanSeconds { get { return _modelCalls > 0 ? _scanSecondsTotal / _modelCalls : 0f; } }

    public string Status { get; private set; } = "starting…";
    public DyrMode ActiveMode { get; private set; }


    void Start()
    {
        // Unfocused, the Editor stops ticking and the clip stops with it, then jumps when focus
        // comes back. A session being screen-recorded is a session nobody is clicking on.
        Application.runInBackground = true;

        LogEnvironment();

        _client = FindAnyObjectByType<DyrBackendClient>();
        if (_client == null)
        {
            _client = gameObject.AddComponent<DyrBackendClient>();
            _client.SetBaseUrlIfUnset(backendBaseUrl);
        }

        _placer = FindAnyObjectByType<DyrLabelPlacer>();
        if (_placer == null) _placer = gameObject.AddComponent<DyrLabelPlacer>();

        StartCoroutine(Run());
    }

    /// <summary>
    /// One block naming everything a failed session usually turns out to be missing.
    /// </summary>
    /// <remarks>
    /// A headset gives no console, so whatever is not logged here has to be guessed at from a
    /// blank screen. Each line is something that has actually caused one.
    /// </remarks>
    void LogEnvironment()
    {
        // Every probe below can throw on a runtime that is still starting, and a diagnostic that
        // takes the app down with it is worse than no diagnostic: the whole body is guarded.
        try
        {
            DyrLog.Info(Tag, "======================= DYR STARTUP =======================");
            DyrLog.Info(Tag, "Platform " + Application.platform + " | Unity " + Application.unityVersion
                + " | mode requested: " + mode);
            DyrLog.Info(Tag, "XR device active: " + Probe(() =>
                UnityEngine.XR.XRSettings.isDeviceActive + " | loaded device: '"
                + UnityEngine.XR.XRSettings.loadedDeviceName + "'"));

#if DYR_META_SDK
            DyrLog.Info(Tag, "Meta SDK: compiled in (DYR_META_SDK)");
            DyrLog.Info(Tag, "HMD present: " + Probe(() => OVRManager.isHmdPresent.ToString())
                + " | passthrough supported: " + Probe(() => OVRManager.IsInsightPassthroughSupported().ToString()));
            DyrLog.Info(Tag, "Camera permission (" + DyrPassthroughFrameSource.HeadsetCameraPermissionName
                + "): " + Probe(() => DyrPassthroughFrameSource.HasCameraPermission
                    ? "GRANTED" : "NOT GRANTED YET"));
#else
            DyrLog.Warn(Tag, "Meta SDK NOT compiled in: no passthrough, no anchors. Build with "
                + "Tools > DYR > Enable Meta Support if this is a headset build.");
#endif

            DyrLog.Info(Tag, "Backend fallback URL: " + backendBaseUrl
                + (Application.platform == RuntimePlatform.Android
                    && (backendBaseUrl.Contains("127.0.0.1") || backendBaseUrl.Contains("localhost"))
                    ? "  <-- localhost means THIS HEADSET; discovery has to find the PC instead" : ""));
            DyrLog.Info(Tag, "Input: " + Probe(DyrInput.BackendDescription)
                + " | scan = A button or right-hand pinch, auto-scan = B or left-hand pinch");
            DyrLog.Info(Tag, "==========================================================");
        }
        catch (Exception error)
        {
            Debug.LogWarning("[DYR] Startup diagnostics failed (" + error.Message
                + ") - carrying on, this block only reports state");
        }
    }

    /// <summary>Read one diagnostic value, reporting a throw instead of propagating it.</summary>
    static string Probe(Func<string> read)
    {
        try { return read(); }
        catch (Exception error) { return "unavailable (" + error.GetType().Name + ")"; }
    }

    /// <summary>Seconds a source is given to produce its first frame before Auto moves on.</summary>
    const float SourceReadySeconds = 15f;

    /// <summary>Seconds the Meta runtime is given to come up before we believe its first answer.</summary>
    const float PassthroughRuntimeSeconds = 8f;

    /// <summary>
    /// Give the headset runtime time to start before asking whether passthrough exists.
    /// </summary>
    /// <remarks>
    /// OVRManager reports no passthrough until it has initialised, and a wrong "no" here is
    /// permanent: Auto picks the virtual room and never reconsiders. Returns as soon as the
    /// answer is yes, so a PC session is not delayed by the timeout.
    /// </remarks>
    IEnumerator WaitForPassthroughRuntime()
    {
        if (mode != DyrMode.Auto && mode != DyrMode.Passthrough) yield break;

        float deadline = Time.realtimeSinceStartup + PassthroughRuntimeSeconds;
        while (Time.realtimeSinceStartup < deadline)
        {
            bool available = false;
            try { available = DyrMetaBridge.PassthroughAvailable; }
            catch (Exception) { /* runtime still starting: keep waiting */ }

            if (available)
            {
                DyrLog.Info(Tag, "Passthrough runtime came up after "
                    + (PassthroughRuntimeSeconds - (deadline - Time.realtimeSinceStartup)).ToString("F1") + "s");
                yield break;
            }
            yield return new WaitForSecondsRealtime(0.25f);
        }

        DyrLog.Info(Tag, "No passthrough after " + PassthroughRuntimeSeconds
            + "s -> this is either a PC session or a headset without it");
    }

    /// <summary>Which source to use.</summary>
    DyrMode ResolveMode()
    {
        if (mode != DyrMode.Auto) return mode;
        // Auto's job is to find a mode that works, so a probe that throws is an answer, not an end.
        try
        {
            if (DyrMetaBridge.PassthroughAvailable) return DyrMode.Passthrough;
        }
        catch (Exception error)
        {
            DyrLog.Warn(Tag, "Probing for passthrough threw (" + error.Message
                + ") -> Auto is taking that as a no and playing the video instead");
        }
        return DyrMode.Video;
    }

    /// <summary>Auto's next try. Passthrough falls to Vr: a screen overlay is no use in a headset.</summary>
    static DyrMode FallbackFor(DyrMode failed)
    {
        if (failed == DyrMode.Passthrough) return DyrMode.Vr;
        return DyrMode.Video;
    }

    IDyrFrameSource CreateSource(DyrMode resolved)
    {
        // The world-space panel is the ONLY status a wearer can read: the OnGUI box below is a
        // flat-screen overlay and never reaches a headset. Passthrough needs it as much as Vr does.
        if (showVrHud && (resolved == DyrMode.Vr || resolved == DyrMode.Passthrough))
            DyrVrHud.Ensure(gameObject);

        if (resolved == DyrMode.Vr)
        {
            // Prepare raycastable geometry before the first capture.
            DyrVrEnvironment.Ensure(gameObject);
            return gameObject.AddComponent<DyrVrSceneFrameSource>();
        }

        if (resolved == DyrMode.Passthrough)
        {
            // The wearer has to SEE the room the labels are attached to.
            if (FindAnyObjectByType<DyrPassthroughSetup>() == null)
                gameObject.AddComponent<DyrPassthroughSetup>();
            return gameObject.AddComponent<DyrPassthroughFrameSource>();
        }

        // Only when asked for: the mp4 is the normal path now that the clock is set explicitly.
        if (useFrameSequence && Directory.Exists(DyrFrameSequenceSource.FolderFor(videoFileName)))
        {
            DyrFrameSequenceSource frames = gameObject.AddComponent<DyrFrameSequenceSource>();
            frames.clipName = videoFileName;
            frames.loop = loopVideo;
            return frames;
        }

        DyrVideoFrameSource video = gameObject.AddComponent<DyrVideoFrameSource>();
        video.fileName = videoFileName;
        video.loop = loopVideo;
        return video;
    }


    IEnumerator Run()
    {
        // Wait for XR initialization before selecting a source.
        yield return null;
        yield return null;

        // Two frames is not enough on a headset: the Meta runtime answers "no passthrough" until
        // it has finished starting, and Auto then settles on the virtual room for the whole session.
        yield return WaitForPassthroughRuntime();

        ActiveMode = ResolveMode();
        _source = CreateSource(ActiveMode);
        DyrLog.Info(Tag, "Keyboard: " + DyrInput.BackendDescription());
        if (!UnityEngine.XR.XRSettings.isDeviceActive)
            DyrLog.Info(Tag, "No XR device: this is a flat session, and Video and Vr never needed one. "
                + "The red '[MetaXRFeature]: ErrorFormFactorUnavailable' is the Meta plugin saying so.");
        DyrLog.Info(Tag, "Mode " + ActiveMode + " (requested " + mode + ") on " + Application.platform
            + " -> source '" + _source.SourceName + "'"
            + (mode == DyrMode.Auto && ActiveMode == DyrMode.Video
                ? " - no headset passthrough detected, playing the video instead" : ""));

        // Auto means whichever mode WORKS, not whichever one the device claims. A chosen mode fails loudly.
        while (true)
        {
            Status = "waiting for the frame source…";
            float deadline = Time.realtimeSinceStartup + SourceReadySeconds;
            while (!_source.IsReady && Time.realtimeSinceStartup < deadline) yield return null;
            if (_source.IsReady) break;

            DyrMode next = FallbackFor(ActiveMode);
            if (mode != DyrMode.Auto || next == ActiveMode) break;

            DyrLog.Warn(Tag, "'" + _source.SourceName + "' never produced a frame in "
                + SourceReadySeconds + "s -> Auto is falling back from " + ActiveMode
                + " to " + next);
            MonoBehaviour dead = _source as MonoBehaviour;
            if (dead != null) Destroy(dead);
            ActiveMode = next;
            _source = CreateSource(ActiveMode);
            DyrLog.Info(Tag, "Mode is now " + ActiveMode + " -> source '" + _source.SourceName + "'");
        }

        if (!_source.IsReady)
        {
            Status = "frame source never became ready";
            DyrLog.Error(Tag, "The frame source '" + _source.SourceName + "' is still not ready "
                + "after " + SourceReadySeconds + "s -> no scan can run. Check the lines above "
                + "from it."
                + (mode == DyrMode.Auto
                    ? ""
                    : " Mode is set to " + mode + " by hand, so nothing else was tried: put it "
                        + "back to Auto and it will fall through to one that works."));
            yield break;
        }

        // No request may go to an address discovery is about to replace.
        while (!_client.AddressResolved) yield return null;

        // Only this world's labels, so a VR run does not come up wearing the real room's.
        yield return _client.GetKnownObjects(
            _source.SourceName,
            known =>
            {
                int count = known.objects != null ? known.objects.Length : 0;
                DyrLog.Info(Tag, "Backend knows " + count + " object(s) here from previous sessions");
                _placer.RestoreAnchored(known.objects);
            },
            error => DyrLog.Warn(Tag, "Could not load known objects: " + error
                + " (labels will still appear as the model recognises things)"));

        // Now, not at Start: the room scan is not loaded until the Meta runtime is up.
        if (ActiveMode == DyrMode.Passthrough) DyrMetaBridge.LogRoomContents();

        Status = "ready";
        // Started here, not at zero: the clock counts the domain reload, so scan one fired instantly.
        _lastScanSeconds = Time.realtimeSinceStartup;
        IDyrFinishingSource finishing = _source as IDyrFinishingSource;
        bool announcedTheEnd = false;
        while (true)
        {
            // A clip that has run out ends the session. Auto scan goes off, the keys keep working.
            if (finishing != null && finishing.Finished && !announcedTheEnd)
            {
                announcedTheEnd = true;
                autoScan = false;
                Status = "clip finished - " + _scanCount + " frame(s) scanned, auto scan off";
                DyrLog.Info(Tag, Status + ". Space scans that last frame again; everything the "
                    + "run documented is on the backend at GET /objects");
            }

            if (autoScan && !_scanning
                && SecondsSince(_lastScanSeconds) >= scanPauseSeconds)
            {
                yield return ScanOnce();
            }
            yield return null;
        }
    }

    void Update()
    {
        if (DyrInput.ScanPressed()) ScanNow();
        if (DyrInput.ToggleAutoScanPressed())
        {
            autoScan = !autoScan;
            Status = "auto scan " + (autoScan ? "ON" : "OFF");
            DyrLog.Info(Tag, Status);
        }
    }

    /// <summary>Stopped mid-scan: the coroutine dies where it stands, so the view is freed here.</summary>
    void OnDisable()
    {
        if (!_scanning) return;
        _scanning = false;
        // These labels were measured on a frame the source has long left behind.
        if (_placer != null) _placer.HideScreenLabels();
    }

    /// <summary>Scan immediately (Space, controller A, or a UI button).</summary>
    public void ScanNow()
    {
        if (_scanning)
        {
            Status = "busy: the previous frame is still being analysed";
            return;
        }
        if (_source == null || !_source.IsReady)
        {
            Status = "the frame source is not ready yet";
            return;
        }
        StartCoroutine(ScanOnce());
    }

    IEnumerator ScanOnce()
    {
        _scanning = true;
        // Stamped here, at the start: left over, they make a finished scan look like it is still working.
        _scanStarted = Time.realtimeSinceStartup;
        ScanLabelCount = 0;

        ScanPhase = "looking for a sharp frame";
        Status = "looking for a sharp frame…";

        byte[] frame = null;
        yield return CaptureSharpestFrame(jpg => frame = jpg);

        // The pose the shutter fired from, kept because the answer arrives long after it.
        IDyrFrameViewSource viewSource = _source as IDyrFrameViewSource;
        DyrCaptureView captureView = viewSource != null
            ? viewSource.CaptureView
            : DyrCaptureView.From(DyrCameras.Head);

        if (frame == null)
        {
            // "Not ready" on its own points at nothing; the source knows which condition failed.
            DyrPassthroughFrameSource passthrough = _source as DyrPassthroughFrameSource;
            string reason = passthrough != null ? passthrough.WhyNotReady() : "source not ready";
            Status = "capture failed: " + reason;
            DyrLog.Error(Tag, "Capture produced no frame -> " + reason);

            // A camera that died mid-session stays dead until something reopens it, and every
            // later scan would fail the same way.
            if (passthrough != null) passthrough.Revive();

            _scanning = false;
            yield break;
        }

        _scanCount++;

        // Where the picture is now, so the seconds of source an answer costs end up on the record.
        IDyrProgressSource progress = _source as IDyrProgressSource;
        float positionBefore = progress != null ? progress.PositionSeconds : 0f;

        // One read per frame: what is here and what we already know are one question, asked once.
        Status = "analysing frame " + _scanCount + "…";
        float started = Time.realtimeSinceStartup;
        int count = 0;
        ScanPhase = "reading the frame";

        // Draw each streamed label as soon as it arrives.
        _placer.BeginScan(_source);
        yield return _client.ScanStream(
            frame, _source.SourceName, DyrMetaBridge.CurrentRoomId(),
            label =>
            {
                count++;
                _placer.ApplyOne(label, captureView);
                ScanLabelCount = count;
                ScanPhase = label.is_new ? "documenting" : "recognising";
                Status = "frame " + _scanCount + " · " + count + " label(s) in "
                    + SecondsSince(started).ToString("F1") + "s";
            },
            done =>
            {
                float seconds = SecondsSince(started);
                if (!done.ok)
                {
                    Status = done.reason;
                    DyrLog.Info(Tag, "Scan skipped by the backend: " + done.reason);
                    return;
                }
                _modelCalls++;
                _scanSecondsTotal += seconds;
                Status = count + " label(s) in " + seconds.ToString("F1") + "s";
                DyrLog.Info(Tag, "Frame " + _scanCount + " -> " + Status);
            },
            error =>
            {
                _failCount++;
                Status = "scan failed: " + error;
                // What it did draw is as true as a whole answer: it stands and times out like the rest.
                DyrLog.Error(Tag, "Frame " + _scanCount + " FAILED after "
                    + SecondsSince(started).ToString("F1") + "s: " + error
                    + " (the " + count + " label(s) already drawn stay up)");
            });

        if (progress != null)
        {
            float moved = progress.PositionSeconds - positionBefore;
            // Round the loop point the position comes back smaller; that is a wrap, not a stall.
            if (moved < 0f) moved += progress.LengthSeconds;
            DyrLog.Info(Tag, "The answer cost " + moved.ToString("F1") + "s of source -> "
                + progress.ProgressLine);
        }

        // Counted from the END of the scan, not the start.
        _lastScanSeconds = Time.realtimeSinceStartup;
        _scanning = false;
    }

    /// <summary>Spend the model call on the sharpest frame in a short window.</summary>
    IEnumerator CaptureSharpestFrame(Action<byte[]> onDone)
    {
        float best = -1f;
        byte[] bestFrame = null;
        float end = Time.realtimeSinceStartup + Mathf.Max(0f, focusWindowSeconds);

        do
        {
            float score = _source.FocusScore();
            // Re-encode only meaningful focus improvements.
            if (score > best * 1.1f)
            {
                byte[] candidate = _source.CaptureJpeg();
                if (candidate != null)
                {
                    best = score;
                    bestFrame = candidate;
                }
            }
            yield return null;
        }
        while (Time.realtimeSinceStartup < end);

        onDone(bestFrame);
    }

    static Texture2D _pixel;
    static GUIStyle _textStyle;

    /// <summary>One white pixel, tinted by GUI.color wherever a flat block is drawn.</summary>
    static Texture2D Pixel()
    {
        if (_pixel != null) return _pixel;
        _pixel = new Texture2D(1, 1);
        _pixel.SetPixel(0, 0, Color.white);
        _pixel.Apply();
        return _pixel;
    }

    static void Fill(Rect rect, Color colour)
    {
        Color previous = GUI.color;
        GUI.color = colour;
        GUI.DrawTexture(rect, Pixel());
        GUI.color = previous;
    }

    /// <summary>Text sized from the window: the default skin's font is unreadable in a recording.</summary>
    static GUIStyle TextStyle()
    {
        if (_textStyle == null)
        {
            _textStyle = new GUIStyle(GUI.skin.label);
            _textStyle.wordWrap = false;
            _textStyle.alignment = TextAnchor.MiddleLeft;
            _textStyle.padding = new RectOffset(0, 0, 0, 0);
            _textStyle.normal.textColor = Color.white;
        }
        _textStyle.fontSize = Mathf.Clamp(Mathf.RoundToInt(Screen.height * 0.020f), 13, 34);
        return _textStyle;
    }

    /// <summary>Band across the top while a frame is with the model: the answer takes seconds.</summary>
    void DrawScanBar(GUIStyle style)
    {
        if (!IsScanning || ScanSeconds < 0.2f) return;

        float height = style.fontSize * 2.2f;
        float width = Screen.width;
        Fill(new Rect(0f, 0f, width, height), new Color(0f, 0f, 0f, 0.62f));

        // No total to divide by: fill over a typical scan and stop short of the end.
        float fraction = Mathf.Clamp01(ScanSeconds / 20f) * 0.98f;
        Fill(new Rect(0f, height - 4f, width * fraction, 4f), new Color(0.30f, 0.75f, 1f, 0.9f));

        GUI.Label(new Rect(style.fontSize, 0f, width - style.fontSize, height),
            ScanPhase + "…   " + ScanSeconds.ToString("F0") + "s"
            + (ScanLabelCount > 0 ? "   ·   " + ScanLabelCount + " object(s) so far" : ""), style);
    }

    readonly List<string> _statusLines = new List<string>();

    /// <summary>A row the box can fit on one line; the whole message is in the log anyway.</summary>
    static string Row(string text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max) return text;
        return text.Substring(0, max - 1) + "…";
    }

    void BuildStatusLines()
    {
        _statusLines.Clear();
        _statusLines.Add("DYR " + ActiveMode + "   ·   "
            + (_source != null ? _source.SourceName : "starting") + "   ·   "
            + (_placer != null ? _placer.VisibleLabelCount : 0) + " label(s) up");
        _statusLines.Add(Row(_client != null ? _client.LlmStatusLine : "no backend client", 78));
        IDyrProgressSource progress = _source as IDyrProgressSource;
        if (progress != null) _statusLines.Add(progress.ProgressLine);
        if (_modelCalls > 0)
            _statusLines.Add("answers in " + AverageScanSeconds.ToString("F1") + "s on average"
                + "   ·   " + _modelCalls + " call(s) over " + _scanCount + " frame(s)"
                + (_failCount > 0 ? "   ·   " + _failCount + " failed" : ""));
        _statusLines.Add(Row(Status, 78));
        _statusLines.Add("Space / A / right middle-pinch = scan   ·   N / B / left middle-pinch = auto "
            + (autoScan ? "ON" : "OFF"));

        // The one line worth interrupting the status for: a headset shows no console, so an
        // exception is otherwise invisible.
        if (!string.IsNullOrEmpty(DyrLog.LastCrash))
            _statusLines.Add(Row("CRASH: " + DyrLog.LastCrash, 78));
    }

    void OnGUI()
    {
        GUIStyle style = TextStyle();
        DrawScanBar(style);
        if (!showStatusOverlay) return;

        // Measured first, then the box is cut to fit: a fixed one squashes and clips its own text.
        BuildStatusLines();
        float pad = style.fontSize * 0.8f;
        float line = style.fontSize * 1.55f;
        float text = 0f;
        for (int i = 0; i < _statusLines.Count; i++)
            text = Mathf.Max(text, style.CalcSize(new GUIContent(_statusLines[i])).x);
        text = Mathf.Min(text, Screen.width * 0.72f);

        Rect box = new Rect(pad, style.fontSize * 2.6f,
            text + pad * 2f, _statusLines.Count * line + pad * 2f);
        Fill(box, new Color(0f, 0f, 0f, 0.74f));
        for (int i = 0; i < _statusLines.Count; i++)
            GUI.Label(new Rect(box.x + pad, box.y + pad + i * line, text, line),
                _statusLines[i], style);
    }
}
