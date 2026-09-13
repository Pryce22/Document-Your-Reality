using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

/// <summary>Plays a clip as a folder of JPEGs, decoded here: no media stack to stall.</summary>
public class DyrFrameSequenceSource : MonoBehaviour,
    IDyrFrameSource, IDyrFinishingSource, IDyrProgressSource
{
    const string Tag = "frames";

    [Tooltip("Clip the folder was extracted from: names the folder (test.mp4 -> test_frames) "
        + "and the source the backend groups objects under.")]
    public string clipName = "test.mp4";
    [Tooltip("Frames shown per second. Match what the folder was extracted at, or it runs fast.")]
    public float fps = 12f;
    [Tooltip("Start over at the end. Off for a recording: the take is one pass and ends on a still.")]
    public bool loop = true;
    [Tooltip("Fill the view instead of letterboxing. Costs the aspect ratio; the model gets the original.")]
    public bool stretchToFill = true;

    /// <summary>Every frame's JPEG bytes, read once. 23 MB for a 79-second clip at 12 fps, and it
    /// buys a scan that hands the model bytes it already has instead of encoding a new picture.</summary>
    readonly List<byte[]> _frames = new List<byte[]>();
    Texture2D _texture;
    RawImage _display;
    RectTransform _canvasRect;
    float _startedAt;
    int _shown = -1;

    /// <summary>Where the frames of a clip in StreamingAssets would be, whether or not they exist.</summary>
    public static string FolderFor(string clip)
    {
        return Path.Combine(Application.streamingAssetsPath,
            Path.GetFileNameWithoutExtension(clip) + "_frames");
    }

    public bool IsReady { get { return _frames.Count > 0 && _texture != null; } }
    public string SourceName { get { return "video:" + clipName; } }
    public bool Finished { get; private set; }

    public float PositionSeconds { get { return fps > 0.01f ? Index / fps : 0f; } }
    public float LengthSeconds { get { return fps > 0.01f ? _frames.Count / fps : 0f; } }

    public string ProgressLine
    {
        get
        {
            if (!IsReady) return "frames: still loading…";
            return "clip " + PositionSeconds.ToString("F1") + "s / " + LengthSeconds.ToString("F1")
                + "s   ·   frame " + (Index + 1) + "/" + _frames.Count
                + "   ·   " + fps.ToString("F0") + " fps"
                + (Finished ? "   ·   finished" : "");
        }
    }

    /// <summary>Which frame the wall clock is on. Unscaled: a paused game must not stop the clip.</summary>
    int Index
    {
        get
        {
            if (_frames.Count == 0) return 0;
            int elapsed = Mathf.FloorToInt(Mathf.Max(0f, Time.unscaledTime - _startedAt) * fps);
            if (loop) return elapsed % _frames.Count;
            return Mathf.Min(elapsed, _frames.Count - 1);
        }
    }

    // Start, not Awake: DyrScanLoop adds this component and only then sets clipName.
    void Start()
    {
        BuildDisplay();
        Load();
        _startedAt = Time.unscaledTime;
    }

    void Load()
    {
        string folder = FolderFor(clipName);
        if (!Directory.Exists(folder))
        {
            DyrLog.Error(Tag, "No frame folder at " + folder + " -> nothing to play. Extract one "
                + "with: ffmpeg -i " + clipName + " -vf fps=12 -q:v 5 f_%05d.jpg");
            return;
        }

        string[] files = Directory.GetFiles(folder, "*.jpg");
        System.Array.Sort(files);           // f_00001, f_00002, … in the order they were shot
        foreach (string file in files) _frames.Add(File.ReadAllBytes(file));

        if (_frames.Count == 0)
        {
            DyrLog.Error(Tag, "The frame folder " + folder + " holds no .jpg at all");
            return;
        }

        _texture = new Texture2D(2, 2, TextureFormat.RGB24, false);
        Show(0);
        DyrLog.Info(Tag, "Loaded " + _frames.Count + " frame(s) from " + folder + " -> "
            + LengthSeconds.ToString("F1") + "s at " + fps.ToString("F0") + " fps"
            + (loop ? " (looping)" : "") + ", " + _texture.width + "x" + _texture.height);
    }

    void BuildDisplay()
    {
        GameObject canvasObject = new GameObject("DyrFrameCanvas");
        canvasObject.transform.SetParent(transform, false);
        Canvas canvas = canvasObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = -10;  // labels draw above the picture
        _canvasRect = (RectTransform)canvasObject.transform;

        GameObject imageObject = new GameObject("Frame", typeof(RectTransform));
        imageObject.transform.SetParent(canvasObject.transform, false);
        _display = imageObject.AddComponent<RawImage>();
        _display.rectTransform.anchorMin = Vector2.zero;
        _display.rectTransform.anchorMax = Vector2.zero;
        _display.rectTransform.pivot = Vector2.zero;
    }

    /// <summary>Decode one frame into the texture on screen. Only ever called when it changes.</summary>
    void Show(int index)
    {
        if (index == _shown || index < 0 || index >= _frames.Count) return;
        _texture.LoadImage(_frames[index], markNonReadable: false);
        _display.texture = _texture;
        _shown = index;
    }

    void LateUpdate()
    {
        if (!IsReady) return;

        int index = Index;
        Show(index);
        if (!loop && !Finished && index >= _frames.Count - 1)
        {
            Finished = true;
            DyrLog.Info(Tag, "Sequence finished on frame " + _frames.Count
                + " - it stays on screen and nothing restarts it");
        }

        Rect rect = DisplayRect();
        _display.rectTransform.anchoredPosition = new Vector2(rect.x, rect.y);
        _display.rectTransform.sizeDelta = new Vector2(rect.width, rect.height);
    }

    Rect DisplayRect()
    {
        Vector2 area = DyrFrameUtils.CanvasArea(_canvasRect);
        if (stretchToFill) return new Rect(0f, 0f, area.x, area.y);
        return DyrFrameUtils.Fit(area, (float)_texture.width / _texture.height);
    }

    /// <summary>The frame on screen, as the JPEG it already is: no readback and no re-encode.</summary>
    public byte[] CaptureJpeg()
    {
        if (!IsReady)
        {
            DyrLog.Warn(Tag, "Capture refused: the frames are not loaded yet");
            return null;
        }
        return _frames[Index];
    }

    public float FocusScore() { return IsReady ? DyrFrameUtils.FocusScore(_texture) : 0f; }

    public bool TryGetDisplayRect(out Rect rect)
    {
        if (!IsReady) { rect = new Rect(); return false; }
        rect = DisplayRect();
        return true;
    }

    void OnDestroy()
    {
        if (_texture != null) Destroy(_texture);
    }
}
