namespace HeadPointerMvp.Domain;

public sealed class AppSettings
{
    public bool Enabled { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public int DwellMilliseconds { get; set; } = 450;
    public int CooldownMilliseconds { get; set; } = 900;
    public float YawThresholdDegrees { get; set; } = 12;
    public float SmoothingFactor { get; set; } = 0.25f;
    public int CenterMonitorIndex { get; set; } = 1;
    public string PoseProvider { get; set; } = "OpenSeeFace";
    public string OpenSeeFaceHost { get; set; } = "127.0.0.1";
    public int OpenSeeFacePort { get; set; } = 11573;
    public int CameraIndex { get; set; }
    public Dictionary<string, float> MonitorYawCalibration { get; set; } = new();
    public bool ActivateWindowOnMove { get; set; } = true;
    public bool AllowSimulatedClickActivation { get; set; } = true;
    public bool NotifyOnMonitorChange { get; set; } = true;
    public bool CameraTrackingEnabled { get; set; }
    public bool VoiceCommandsEnabled { get; set; }
    /// <summary>WASAPI capture device ID for voice commands. Empty means "follow Windows' system default recording device" (SetInputToDefaultAudioDevice).</summary>
    public string MicrophoneDeviceId { get; set; } = string.Empty;
    /// <summary>Ignore head movement while the keyboard/mouse was used recently (looking down at the keys must not move the pointer).</summary>
    public bool FreezeHeadWhileTyping { get; set; } = true;
    public int TypingFreezeMilliseconds { get; set; } = 2500;
    /// <summary>How far (in bridge pitch units) the head may nod from its running baseline before routing is frozen.</summary>
    public float PitchFreezeUnits { get; set; } = 6;
    /// <summary>Opens the numbered zoom-grid for hands-free pointing. Digits 1-9 zoom in, Enter clicks, Esc cancels.</summary>
    public string GridHotkey { get; set; } = "Ctrl+Alt+G";
    /// <summary>Ctrl+Alt+1..9 jump straight to that monitor (1-indexed). Turn off if the combos clash with other software.</summary>
    public bool MonitorHotkeysEnabled { get; set; } = true;
    /// <summary>Mouse side button that opens/closes the grid and is swallowed system-wide: "XButton1" (back), "XButton2" (forward) or "None".</summary>
    public string GridMouseButton { get; set; } = "XButton2";
    /// <summary>Tilt your head sideways and hold to open/close the grid. Works with a remote desktop focused.</summary>
    public bool HeadTiltOpensGrid { get; set; }
    public float TiltDegrees { get; set; } = 15;
    public int TiltHoldMilliseconds { get; set; } = 1200;
    /// <summary>Voice commands only act when preceded by this word ("rama show grid"), so dictation and conversation are never touched. May be two words ("sri ram"). Must exist in the speech model's vocabulary.</summary>
    public string VoiceWakeWord { get; set; } = "rama";
    /// <summary>Spoken phrase -> key combination, e.g. "next tab": "Ctrl+Tab". Said as "rama next tab". Edit in settings.json.</summary>
    public Dictionary<string, string> VoiceMacros { get; set; } = new()
    {
        ["next tab"] = "Ctrl+Tab",
        ["previous tab"] = "Ctrl+Shift+Tab",
        ["close tab"] = "Ctrl+W",
        ["new tab"] = "Ctrl+T",
        ["switch window"] = "Alt+Tab",
        ["copy"] = "Ctrl+C",
        ["paste"] = "Ctrl+V",
        ["select all"] = "Ctrl+A",
        ["save"] = "Ctrl+S",
        ["undo that"] = "Ctrl+Z"
    };
    public string ToggleHotkey { get; set; } = "Ctrl+Alt+H";
    public string PauseHotkey { get; set; } = "Ctrl+Alt+P";
}
