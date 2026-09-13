using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>Normalizes input across Unity backends and the XR Simulator.</summary>
public static class DyrInput
{
    /// <summary>
    /// Scan this frame: Space / 3 on a keyboard, A on a Touch controller, right middle-finger pinch.
    /// </summary>
    /// <remarks>
    /// The MIDDLE finger, not the index: an index pinch is the system's own click gesture, and
    /// binding to it opens the Meta menu instead of scanning.
    /// </remarks>
    public static bool ScanPressed()
    {
#if DYR_META_SDK
        if (OVRInput.GetDown(OVRInput.Button.One)) return true;
        if (PinchStarted(OVRPlugin.Hand.HandRight)) return true;
#endif
        return GetKeyDown(KeyCode.Space);
    }

    /// <summary>
    /// Show or hide the in-headset log: L, Y on a Touch controller, or both ring-finger pinches.
    /// </summary>
    /// <remarks>
    /// Worth its own binding because scoped storage keeps the log file out of the headset's Files
    /// app: on-device, the panel is the only way to read what happened.
    /// </remarks>
    public static bool ToggleLogPressed()
    {
#if DYR_META_SDK
        if (OVRInput.GetDown(OVRInput.Button.Four)) return true;
        if (RingPinchStarted()) return true;
#endif
        return GetKeyDown(KeyCode.L);
    }

    /// <summary>
    /// Toggle automatic scanning: N / 4, B on a Touch controller, left middle-finger pinch.
    /// </summary>
    public static bool ToggleAutoScanPressed()
    {
#if DYR_META_SDK
        if (OVRInput.GetDown(OVRInput.Button.Two)) return true;
        if (PinchStarted(OVRPlugin.Hand.HandLeft)) return true;
#endif
        return GetKeyDown(KeyCode.N);
    }

#if DYR_META_SDK
    static bool _leftPinched;
    static bool _rightPinched;

    // Reused across frames: GetHandState fills these arrays, and a fresh state each frame would
    // make it allocate them again every time.
    static OVRPlugin.HandState _leftState;
    static OVRPlugin.HandState _rightState;

    /// <summary>Off for the rest of the session once the runtime has refused once.</summary>
    static bool _handTrackingUsable = true;

    static bool _ringPinched;

    /// <summary>Ring-finger pinch on either hand: the gesture kept for the log view.</summary>
    static bool RingPinchStarted()
    {
        if (!_handTrackingUsable) return false;

        bool pinching = false;
        try
        {
            if (OVRPlugin.GetHandState(OVRPlugin.Step.Render, OVRPlugin.Hand.HandRight, ref _rightState)
                && (_rightState.Status & OVRPlugin.HandStatus.HandTracked) != 0)
                pinching = (_rightState.Pinches & OVRPlugin.HandFingerPinch.Ring) != 0;

            if (!pinching
                && OVRPlugin.GetHandState(OVRPlugin.Step.Render, OVRPlugin.Hand.HandLeft, ref _leftState)
                && (_leftState.Status & OVRPlugin.HandStatus.HandTracked) != 0)
                pinching = (_leftState.Pinches & OVRPlugin.HandFingerPinch.Ring) != 0;
        }
        catch (System.Exception)
        {
            _handTrackingUsable = false;
            return false;
        }

        bool started = pinching && !_ringPinched;
        _ringPinched = pinching;
        return started;
    }

    /// <summary>Index pinch as a button: true only on the frame the fingers close.</summary>
    /// <remarks>
    /// Hands are the only input when no controller is paired, so a scan has to be reachable
    /// without one. Queried every frame, so one failure disables it rather than throwing
    /// sixty times a second.
    /// </remarks>
    static bool PinchStarted(OVRPlugin.Hand hand)
    {
        if (!_handTrackingUsable) return false;

        bool pinching = false;
        try
        {
            bool isLeft = hand == OVRPlugin.Hand.HandLeft;
            if (isLeft)
            {
                if (OVRPlugin.GetHandState(OVRPlugin.Step.Render, hand, ref _leftState)
                    && (_leftState.Status & OVRPlugin.HandStatus.HandTracked) != 0)
                    pinching = (_leftState.Pinches & OVRPlugin.HandFingerPinch.Middle) != 0;
            }
            else
            {
                if (OVRPlugin.GetHandState(OVRPlugin.Step.Render, hand, ref _rightState)
                    && (_rightState.Status & OVRPlugin.HandStatus.HandTracked) != 0)
                    pinching = (_rightState.Pinches & OVRPlugin.HandFingerPinch.Middle) != 0;
            }
        }
        catch (System.Exception error)
        {
            _handTrackingUsable = false;
            DyrLog.Warn("input", "Hand tracking is unavailable (" + error.Message
                + ") -> pinch disabled for this session; use the controller buttons");
            return false;
        }

        bool wasPinched = hand == OVRPlugin.Hand.HandLeft ? _leftPinched : _rightPinched;
        if (hand == OVRPlugin.Hand.HandLeft) _leftPinched = pinching;
        else _rightPinched = pinching;

        return pinching && !wasPinched;
    }
#endif

    /// <summary>True on the frame the key (or its numeric alternate) was pressed.</summary>
    public static bool GetKeyDown(KeyCode key)
    {
        if (Pressed(key)) return true;
        KeyCode alt = Alternate(key);
        return alt != KeyCode.None && Pressed(alt);
    }

    /// <summary>Numeric stand-in for keys the Meta XR Simulator swallows.</summary>
    static KeyCode Alternate(KeyCode key)
    {
        switch (key)
        {
            case KeyCode.Space: return KeyCode.Alpha3;
            case KeyCode.N:     return KeyCode.Alpha4;
            case KeyCode.H:     return KeyCode.Alpha0;
            default:            return KeyCode.None;
        }
    }

    static bool Pressed(KeyCode key)
    {
#if ENABLE_INPUT_SYSTEM
        Keyboard kb = Keyboard.current;
        if (kb != null)
        {
            switch (key)
            {
                case KeyCode.N:      return kb.nKey.wasPressedThisFrame;
                case KeyCode.H:      return kb.hKey.wasPressedThisFrame;
                case KeyCode.Space:  return kb.spaceKey.wasPressedThisFrame;
                case KeyCode.Alpha0: return kb.digit0Key.wasPressedThisFrame;
                case KeyCode.Alpha3: return kb.digit3Key.wasPressedThisFrame;
                case KeyCode.Alpha4: return kb.digit4Key.wasPressedThisFrame;
                // Unmapped key: fall through to the legacy backend (if enabled).
            }
        }
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
        return Input.GetKeyDown(key);
#else
        return false;
#endif
    }

    /// <summary>Which keyboard backend is answering — logged once at startup for diagnostics.</summary>
    public static string BackendDescription()
    {
#if ENABLE_INPUT_SYSTEM && ENABLE_LEGACY_INPUT_MANAGER
        return "Input System (new) + legacy Input Manager (Both)";
#elif ENABLE_INPUT_SYSTEM
        return "Input System (new) only";
#elif ENABLE_LEGACY_INPUT_MANAGER
        return "legacy Input Manager only";
#else
        return "NO input backend enabled (?)";
#endif
    }
}
