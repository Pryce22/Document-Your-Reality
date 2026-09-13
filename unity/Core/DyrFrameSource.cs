using UnityEngine;

/// <summary>Where the frames analysed by the backend come from.</summary>
public interface IDyrFrameSource
{
    /// <summary>False while the video is still opening or the camera negotiating.</summary>
    bool IsReady { get; }

    /// <summary>Sent to the backend so objects stay grouped per session/video.</summary>
    string SourceName { get; }

    /// <summary>Current frame as JPEG bytes, or null if not ready.</summary>
    byte[] CaptureJpeg();

    /// <summary>Rough focus measure of the current frame, higher is sharper.</summary>
    float FocusScore();

    /// <summary>The screen rectangle the frame is displayed in, for 2D label placement.</summary>
    bool TryGetDisplayRect(out Rect rect);
}


/// <summary>The camera view a frame was captured from, frozen at the shutter.</summary>
public struct DyrCaptureView
{
    public Vector3 position;
    public Quaternion rotation;
    public float verticalFov;   // degrees
    public float aspect;        // width / height
    public bool valid;
    public IDyrCapturedRayProvider rayProvider;

    public static DyrCaptureView From(Camera camera)
    {
        DyrCaptureView view = new DyrCaptureView();
        if (camera == null) return view;    // valid stays false
        view.position = camera.transform.position;
        view.rotation = camera.transform.rotation;
        view.verticalFov = camera.fieldOfView;
        view.aspect = camera.aspect > 0.01f ? camera.aspect : 4f / 3f;
        view.valid = true;
        return view;
    }

    /// <summary>The ray Camera.ViewportPointToRay would have returned at capture time.</summary>
    public Ray ViewportPointToRay(Vector2 viewport)
    {
        Ray calibratedRay;
        if (rayProvider != null && rayProvider.TryGetCaptureRay(viewport, out calibratedRay))
            return calibratedRay;

        float tanHalf = Mathf.Tan(verticalFov * 0.5f * Mathf.Deg2Rad);
        Vector3 direction = new Vector3(
            (viewport.x - 0.5f) * 2f * tanHalf * aspect,
            (viewport.y - 0.5f) * 2f * tanHalf,
            1f);
        return new Ray(position, (rotation * direction).normalized);
    }

}


/// <summary>Projects image points through the calibrated pose of a captured frame.</summary>
public interface IDyrCapturedRayProvider
{
    bool TryGetCaptureRay(Vector2 viewport, out Ray ray);
}


/// <summary>A source that knows the exact camera the frame it just handed over was taken with.</summary>
public interface IDyrFrameViewSource
{
    /// <summary>The view the most recent frame was rendered from.</summary>
    DyrCaptureView CaptureView { get; }
}


/// <summary>A source that can say where it has got to, so a stalled picture shows up on screen.</summary>
public interface IDyrProgressSource
{
    /// <summary>One short row: where the source is and how fast it is running.</summary>
    string ProgressLine { get; }

    /// <summary>Seconds into the material, and its total, for measuring what an answer cost.</summary>
    float PositionSeconds { get; }
    float LengthSeconds { get; }
}


/// <summary>A source that runs out: a clip has a last frame, a camera does not.</summary>
public interface IDyrFinishingSource
{
    /// <summary>The clip has reached its last frame and is not going round again.</summary>
    bool Finished { get; }
}


/// <summary>Shared readback helpers for the frame sources.</summary>
public static class DyrFrameUtils
{
    const int FocusSize = 48;
    static Texture2D _focusBuffer;

    /// <summary>Sum of neighbour differences on a 48x48 downscale of the texture.</summary>
    public static float FocusScore(Texture source)
    {
        if (source == null || source.width < 8) return 0f;
        RenderTexture rt = RenderTexture.GetTemporary(FocusSize, FocusSize, 0);
        RenderTexture previous = RenderTexture.active;
        try
        {
            Graphics.Blit(source, rt);
            RenderTexture.active = rt;
            if (_focusBuffer == null)
                _focusBuffer = new Texture2D(FocusSize, FocusSize, TextureFormat.RGB24, false);
            _focusBuffer.ReadPixels(new Rect(0, 0, FocusSize, FocusSize), 0, 0);
            _focusBuffer.Apply(false);
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
        }

        Color32[] pixels = _focusBuffer.GetPixels32();
        float score = 0f;
        for (int y = 1; y < FocusSize; y++)
        {
            for (int x = 1; x < FocusSize; x++)
            {
                int i = y * FocusSize + x;
                score += Mathf.Abs(pixels[i].g - pixels[i - 1].g)
                       + Mathf.Abs(pixels[i].g - pixels[i - FocusSize].g);
            }
        }
        return score / (FocusSize * FocusSize);
    }

    public static byte[] EncodeRenderTexture(RenderTexture rt, ref Texture2D buffer, int quality)
    {
        if (rt == null) return null;
        RenderTexture previous = RenderTexture.active;
        try
        {
            RenderTexture.active = rt;
            if (buffer == null || buffer.width != rt.width || buffer.height != rt.height)
                buffer = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            buffer.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            buffer.Apply(false);
        }
        finally
        {
            RenderTexture.active = previous;
        }
        return buffer.EncodeToJPG(quality);
    }

    /// <summary>Fits an aspect ratio inside an area.</summary>
    public static Rect Fit(Vector2 area, float aspect)
    {
        float areaAspect = area.x / Mathf.Max(1f, area.y);
        float width, height;
        if (aspect > areaAspect)
        {
            width = area.x;
            height = width / aspect;
        }
        else
        {
            height = area.y;
            width = height * aspect;
        }
        return new Rect((area.x - width) * 0.5f, (area.y - height) * 0.5f, width, height);
    }

    /// <summary>Returns the label's canvas or screen area.</summary>
    public static Vector2 CanvasArea(Transform canvasChildOrSelf)
    {
        RectTransform rect = canvasChildOrSelf as RectTransform;
        if (rect != null && rect.rect.width > 1f) return rect.rect.size;
        return new Vector2(Screen.width, Screen.height);
    }
}
