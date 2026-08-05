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
    public bool CameraTrackingEnabled { get; set; } = true;
    public bool VoiceCommandsEnabled { get; set; }
    /// <summary>WASAPI capture device ID for voice commands. Empty means "follow Windows' system default recording device" (SetInputToDefaultAudioDevice).</summary>
    public string MicrophoneDeviceId { get; set; } = string.Empty;
    public string ToggleHotkey { get; set; } = "Ctrl+Alt+H";
    public string PauseHotkey { get; set; } = "Ctrl+Alt+P";
}
