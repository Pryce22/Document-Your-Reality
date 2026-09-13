using System.Collections;
using UnityEngine;

/// <summary>Turns on the compositor layer that lets the real world show through.</summary>
public class DyrPassthroughSetup : MonoBehaviour
{
    const string Tag = "passthrough";

#if DYR_META_SDK
    IEnumerator Start()
    {
        // Let OVRManager / OpenXR initialise before asking about capabilities.
        yield return null;
        yield return null;

        if (!OVRManager.isHmdPresent)
        {
            DyrLog.Info(Tag, "No headset present: passthrough layer not created (this is normal on a PC)");
            yield break;
        }

        OVRManager manager = FindFirstObjectByType<OVRManager>();
        if (manager == null)
        {
            DyrLog.Error(Tag, "OVRManager missing from the scene: passthrough cannot be enabled. "
                + "Add OVRCameraRig.");
            yield break;
        }

        bool supported = false;
        float deadline = Time.realtimeSinceStartup + 5f;
        while (!supported && Time.realtimeSinceStartup < deadline)
        {
            try { supported = OVRManager.IsInsightPassthroughSupported(); }
            catch (System.Exception exc)
            {
                DyrLog.Warn(Tag, "Passthrough capability query still pending: " + exc.Message);
            }
            if (!supported) yield return new WaitForSecondsRealtime(0.25f);
        }
        if (!supported)
        {
            DyrLog.Warn(Tag, "This runtime/headset does not expose XR_FB_passthrough -> staying in VR. "
                + "Labels still work, but against the virtual scene, not the real room.");
            yield break;
        }

        // Never enable an extension the active runtime did not advertise.
        manager.isInsightPassthroughEnabled = true;
        if (FindFirstObjectByType<OVRPassthroughLayer>() == null)
            manager.gameObject.AddComponent<OVRPassthroughLayer>();

        // The camera must clear to transparent, or it paints over the underlay.
        Camera cam = DyrCameras.Head;
        if (cam != null)
        {
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.clear;
        }
        DyrLog.Info(Tag, "Passthrough enabled: the real world is visible, labels draw above it");
    }
#else
    IEnumerator Start()
    {
        DyrLog.Info(Tag, "Meta SDK not compiled in (DYR_META_SDK undefined): no passthrough layer. "
            + "Use Tools > DYR > Enable Meta Support before building for the headset.");
        yield break;
    }
#endif
}
