using UnityEngine;

/// <summary>Finds the viewer's camera without depending on the MainCamera tag.</summary>
public static class DyrCameras
{
    const string Tag = "cameras";

    static Camera _cached;
    static bool _warned;

    /// <summary>The camera to treat as the wearer's eyes; null only when there is no camera at all.</summary>
    public static Camera Head
    {
        get
        {
            if (_cached != null && _cached.isActiveAndEnabled) return _cached;

            _cached = Camera.main;
            if (_cached != null) return _cached;

#if DYR_META_SDK
            OVRCameraRig rig = Object.FindAnyObjectByType<OVRCameraRig>();
            if (rig != null && rig.centerEyeAnchor != null)
            {
                _cached = rig.centerEyeAnchor.GetComponent<Camera>();
                if (_cached == null) _cached = rig.centerEyeAnchor.GetComponentInChildren<Camera>();
            }
#endif
            if (_cached == null)
            {
                // Last resort: any enabled camera, which on a rig is an eye.
                Camera[] cameras = Object.FindObjectsByType<Camera>(FindObjectsSortMode.None);
                foreach (Camera candidate in cameras)
                {
                    if (candidate != null && candidate.isActiveAndEnabled) { _cached = candidate; break; }
                }
            }

            if (_cached != null && !_warned)
            {
                _warned = true;
                DyrLog.Warn(Tag, "No camera is tagged MainCamera; using '" + _cached.name
                    + "' instead. Tag CenterEyeAnchor as MainCamera to silence this.");
            }
            else if (_cached == null && !_warned)
            {
                _warned = true;
                DyrLog.Error(Tag, "NO CAMERA IN THE SCENE: nothing can be drawn in front of the "
                    + "wearer and no capture has a pose. Add OVRCameraRig.");
            }
            return _cached;
        }
    }
}
