using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;

/// <summary>Plays a video and serves its frames: the no-headset path. A scan slows it, never stops it.</summary>
public class DyrVideoFrameSource : MonoBehaviour,
    IDyrFrameSource, IDyrFinishingSource, IDyrProgressSource
{
    const string Tag = "video";

    [Tooltip("File name inside Assets/StreamingAssets, e.g. test.mp4")]
    public string fileName = "test.mp4";
    [Tooltip("Restart the clip when it ends. Off for a recording: the take is one pass and ends on a still.")]
    public bool loop = true;
    [Tooltip("Fill the view instead of letterboxing. Costs the aspect ratio; the model gets the original.")]
    public bool stretchToFill = true;
    [Tooltip("LEAVE AT 1. Unity's player handles a fractional speed badly on this backend: 0.70 "
        + "ran, 0.60 and 0.50 stuck on the first frame and then jumped. To watch a clip in slow "
        + "motion, slow the FILE instead - ffmpeg -filter:v setpts=PTS/0.6 - and play it at 1.")]
    [Range(0.1f, 2f)] public float playbackSpeed = 1f;
    [Range(1, 100)] public int jpegQuality = 85;

    VideoPlayer _player;
    RenderTexture _target;
    /// <summary>The last frame of the clip, kept because the target texture stops being written to.</summary>
    RenderTexture _frozen;
    Texture2D _readback;
    RawImage _display;
    RectTransform _canvasRect;
    long _lastFrame = -1L;
    float _lastFrameChanged;
    bool _stallReported;
    bool _speedApplied;

    /// <summary>The clip is over. Nothing may Play() it again: past the end that means frame 0.</summary>
    public bool Finished { get; private set; }

    /// <summary>Shown in the status box, so "the video looks stuck" is answered by the screen itself.</summary>
    public string ProgressLine
    {
        get
        {
            if (_player == null || !_player.isPrepared) return "clip: still opening…";
            return "clip " + PositionSeconds.ToString("F1") + "s / " + LengthSeconds.ToString("F1")
                + "s   ·   frame " + _player.frame + "/" + _player.frameCount
                + "   ·   " + _player.playbackSpeed.ToString("F2") + "x"
                + (Finished ? "   ·   finished" : "");
        }
    }

    /// <summary>Seconds into the clip, counted from the FRAME rather than from VideoPlayer.time:
    /// the clock has been seen reading 0.0s for a whole session over a clip that still reached its
    /// end on time, and the frame number is what the picture actually shows.</summary>
    public float PositionSeconds
    {
        get
        {
            if (_player == null || !_player.isPrepared) return 0f;
            if (_player.frameRate > 0.01f) return (float)(_player.frame / _player.frameRate);
            return (float)_player.time;
        }
    }

    /// <summary>Length of the clip, for reading a position that has wrapped round the loop point.</summary>
    public float LengthSeconds
    {
        get { return _player != null && _player.isPrepared ? (float)_player.length : 0f; }
    }

    /// <summary>Where the frame is drawn: the canvas, not the screen.</summary>
    Vector2 Area { get { return DyrFrameUtils.CanvasArea(_canvasRect); } }

    /// <summary>The rectangle the frame occupies.</summary>
    Rect DisplayRect()
    {
        Vector2 area = Area;
        if (stretchToFill) return new Rect(0f, 0f, area.x, area.y);
        return DyrFrameUtils.Fit(area, (float)_target.width / _target.height);
    }

    public bool IsReady { get { return _player != null && _player.isPrepared && _target != null; } }

    public string SourceName { get { return "video:" + fileName; } }

    // Start, not Awake: DyrScanLoop adds this component and only then sets fileName.
    void Start()
    {
        // Not Path.Combine: on Android this is a jar: URL, and a URL takes forward slashes.
        string url = Application.streamingAssetsPath.TrimEnd('/', '\\') + "/" + fileName.TrimStart('/', '\\');
        DyrLog.Info(Tag, "Opening video " + url);

        _player = gameObject.AddComponent<VideoPlayer>();
        _player.source = VideoSource.Url;
        _player.url = url;
        _player.isLooping = loop;
        _player.renderMode = VideoRenderMode.RenderTexture;
        // THE CLOCK, SAID OUT LOUD. Left alone this can be DSPTime, which is driven by the audio
        // module - and this project turns audio off. On a machine whose audio device reports
        // "SampleRateHz is 0" that clock never ticks, and the player sits on frame 0 for the whole
        // session while every realtime thing around it carries on normally.
        _player.timeUpdateMode = VideoTimeUpdateMode.UnscaledGameTime;
        // Both: None only says where the samples must not go, zero tracks stops the decoding.
        _player.audioOutputMode = VideoAudioOutputMode.None;
        _player.controlledAudioTrackCount = 0;
        _player.playOnAwake = false;
        _player.errorReceived += (vp, message) => DyrLog.Error(Tag,
            "Video error: " + message + " -> is '" + fileName + "' really in Assets/StreamingAssets? "
            + "Unity needs an H.264 MP4; re-encode the file if it refuses to open.");
        _player.prepareCompleted += OnPrepared;
        _player.loopPointReached += OnEndReached;
        _player.Prepare();

        BuildDisplay();
    }

    void OnPrepared(VideoPlayer player)
    {
        int w = (int)player.width;
        int h = (int)player.height;
        // prepareCompleted fires again on a re-prepare, and the old texture would be leaked.
        if (_target != null) _target.Release();
        _target = new RenderTexture(w, h, 0);
        player.targetTexture = _target;
        _display.texture = _target;

        // The count is known only now, and a track switches off only while stopped: here.
        for (ushort track = 0; track < player.audioTrackCount; track++)
            player.EnableAudioTrack(track, false);

        player.Play();
        DyrLog.Info(Tag, "Video ready: " + w + "x" + h + " @" + player.frameRate.ToString("F1")
            + " fps, " + player.frameCount + " frames -> playing" + (loop ? " (looping)" : "")
            + (Mathf.Approximately(playbackSpeed, 1f)
                ? "" : ", slowing to " + playbackSpeed.ToString("F2") + "x once it is running"));
    }

    /// <summary>The clip's last frame: the only moment it is still in the target texture.</summary>
    void OnEndReached(VideoPlayer player)
    {
        if (loop || Finished) return;

        // Believed unless the clip's own clock denies it; an unusable clock is no denial.
        if (player.length > 1.0 && player.time > 0.5 && player.time < player.length - 1.0)
        {
            DyrLog.Warn(Tag, "Ignoring an end of clip reported at " + player.time.ToString("F1")
                + "s of " + player.length.ToString("F1") + "s: the clip is not over, so it "
                + "carries on and so does the scanning");
            return;
        }

        Finished = true;
        if (_target != null)
        {
            if (_frozen == null || _frozen.width != _target.width || _frozen.height != _target.height)
            {
                if (_frozen != null) _frozen.Release();
                _frozen = new RenderTexture(_target.width, _target.height, 0);
            }
            Graphics.Blit(_target, _frozen);
            if (_display != null) _display.texture = _frozen;
        }
        DyrLog.Info(Tag, "Clip finished on its last frame after " + player.frameCount
            + " frame(s) - it stays on screen and nothing restarts it");
    }

    void BuildDisplay()
    {
        GameObject canvasObject = new GameObject("DyrVideoCanvas");
        canvasObject.transform.SetParent(transform, false);
        Canvas canvas = canvasObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = -10;  // labels draw above the video
        _canvasRect = (RectTransform)canvasObject.transform;

        GameObject imageObject = new GameObject("Frame", typeof(RectTransform));
        imageObject.transform.SetParent(canvasObject.transform, false);
        _display = imageObject.AddComponent<RawImage>();
        _display.rectTransform.anchorMin = Vector2.zero;
        _display.rectTransform.anchorMax = Vector2.zero;
        _display.rectTransform.pivot = Vector2.zero;
    }

    void LateUpdate()
    {
        if (!IsReady) return;
        if (Finished)
        {
            // Only the rectangle, in case the window is resized on the last frame.
            LayOutDisplay();
            return;
        }
        // Nothing here pauses the clip any more, so a pause is something else stopping it: undo it.
        if (_player.isPaused) _player.Play();

        ApplyPlaybackSpeed();
        CheckForStall();
        LayOutDisplay();
    }

    /// <summary>Slow the clip only once it is demonstrably running.</summary>
    void ApplyPlaybackSpeed()
    {
        if (_speedApplied || Mathf.Approximately(playbackSpeed, 1f)) return;
        // The clock has to be TICKING first. Set at Play() time, a fractional speed can land on a
        // player that has not started, and then it never does - one frozen frame and no video.
        if (!_player.isPlaying || _player.time <= 0d) return;

        _speedApplied = true;
        _player.playbackSpeed = Mathf.Max(0.01f, playbackSpeed);
        DyrLog.Info(Tag, "Speed set to " + _player.playbackSpeed.ToString("F2")
            + "x, with the clip already running at " + _player.time.ToString("F2") + "s");
    }

    /// <summary>Seconds before a still picture is worth a line in the log. Long enough that an
    /// editor hitch or an unfocused Game view does not trip it.</summary>
    const float StallSeconds = 10f;

    /// <summary>A picture that stops advancing while the player says it is playing. Reported and
    /// nothing more: restarting the player was tried and it costs you your place in the clip, and
    /// the stall this was written for turned out to be timeUpdateMode, fixed above.</summary>
    void CheckForStall()
    {
        if (!_player.isPlaying) return;

        if (_player.frame != _lastFrame)
        {
            _lastFrame = _player.frame;
            _lastFrameChanged = Time.realtimeSinceStartup;
            _stallReported = false;
            return;
        }
        if (_stallReported || Time.realtimeSinceStartup - _lastFrameChanged < StallSeconds) return;

        _stallReported = true;
        DyrLog.Warn(Tag, "'" + fileName + "' says it is playing but has been on frame " + _lastFrame
            + " for " + StallSeconds + "s. If it never moves: the clip must be H.264 baseline with "
            + "no B-frames (the README has the command), and watch for 'Unexpected timestamp values "
            + "detected' higher up.");
    }

    void LayOutDisplay()
    {
        Rect rect = DisplayRect();
        _display.rectTransform.anchoredPosition = new Vector2(rect.x, rect.y);
        _display.rectTransform.sizeDelta = new Vector2(rect.width, rect.height);
    }

    public byte[] CaptureJpeg()
    {
        if (!IsReady)
        {
            DyrLog.Warn(Tag, "Capture refused: the video is not prepared yet");
            return null;
        }
        // Past the end the copy IS the picture, and the target texture is no longer written to.
        return DyrFrameUtils.EncodeRenderTexture(Finished ? _frozen : _target, ref _readback, jpegQuality);
    }

    // Measured on whatever CaptureJpeg would encode, which past the end of the clip is the copy.
    public float FocusScore()
    {
        return IsReady ? DyrFrameUtils.FocusScore(Finished ? _frozen : _target) : 0f;
    }

    public bool TryGetDisplayRect(out Rect rect)
    {
        if (!IsReady) { rect = new Rect(); return false; }
        rect = DisplayRect();
        return true;
    }

    void OnDestroy()
    {
        if (_target != null) _target.Release();
        if (_frozen != null) _frozen.Release();
    }
}
