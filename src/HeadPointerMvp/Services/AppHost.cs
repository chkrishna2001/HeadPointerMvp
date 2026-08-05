using HeadPointerMvp.Domain;
using HeadPointerMvp.Infrastructure;
using HeadPointerMvp;
using System.Diagnostics;

namespace HeadPointerMvp.Services;

public sealed class AppHost : IAsyncDisposable
{
    private readonly SettingsStore _settingsStore = new();
    private readonly FileLogger _logger = new();
    private readonly StartupService _startup = new();
    private readonly MonitorService _monitors = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly JobObject _bridgeJob = new();
    private AppSettings _settings = new();
    private IHeadPoseSource? _poseSource;
    private TrayIconService? _tray;
    private HotKeyService? _hotkeys;
    private SettingsWindow? _settingsWindow;
    private PointerService? _pointer;
    private HeadDirectionRouter? _router;
    private Process? _bridgeProcess;
    private VoiceCommandService? _voice;
    private float? _latestYaw;
    private bool _disposed;

    public AppSettings Settings => _settings;
    public MonitorService Monitors => _monitors;

    public bool TryGetLatestYaw(out float yaw)
    {
        yaw = _latestYaw ?? 0f;
        return _latestYaw.HasValue;
    }

    /// <summary>Pauses/resumes pointer movement without touching the persisted Enabled setting -- used
    /// while the settings window is open so the cursor doesn't jump around mid-calibration.</summary>
    public void SetCalibrationPause(bool paused)
    {
        if (_router is not null) _router.SuppressMovement = paused;
    }

    public static async Task<AppHost> StartAsync()
    {
        var host = new AppHost();
        await host.StartCoreAsync();
        return host;
    }

    private async Task StartCoreAsync()
    {
        _settings = await _settingsStore.LoadAsync();
        _pointer = new PointerService(_monitors, _logger);
        _tray = new TrayIconService(ShowSettings, () => _ = ToggleEnabledAsync(), Exit);

        _router = new HeadDirectionRouter(() => _settings, _pointer, _monitors, _logger);
        _router.SmoothedYawChanged += yaw => _latestYaw = yaw;
        _router.MonitorChanged += OnMonitorChanged;

        await StartPoseSourceAsync(_router);
        await StartHotkeysAsync();
        if (_settings.CameraTrackingEnabled) StartCameraTracking();
        if (_settings.VoiceCommandsEnabled) StartVoiceCommands();
        await _logger.InfoAsync($"Started. Found {_monitors.GetMonitors().Count} monitor(s).");
    }

    private async Task StartPoseSourceAsync(HeadDirectionRouter router)
    {
        try
        {
            _poseSource = new OpenSeeFaceHeadPoseSource(_settings.OpenSeeFaceHost, _settings.OpenSeeFacePort, _logger);
            _poseSource.PoseReceived += (_, pose) => router.Process(pose);
            await _poseSource.StartAsync(_stop.Token);
            _tray?.ShowBalloon("HeadPointer", "Tray app is running. Waiting for head tracking data.");
        }
        catch (Exception ex)
        {
            await _logger.InfoAsync($"Pose receiver failed to start: {ex}");
            _tray?.ShowWarning("HeadPointer needs the tracker", $"Pose receiver did not start: {ex.Message}");
        }
    }

    private async Task StartHotkeysAsync()
    {
        try
        {
            _hotkeys = new HotKeyService();
            _hotkeys.Register(_settings.ToggleHotkey, () => _ = ToggleEnabledAsync());
            _hotkeys.Register(_settings.PauseHotkey, () => _ = PauseAsync());
        }
        catch (Exception ex)
        {
            await _logger.InfoAsync($"Hotkey registration failed: {ex}");
            _tray?.ShowWarning("HeadPointer hotkeys unavailable", ex.Message);
        }
    }

    public async Task SaveSettingsAsync(AppSettings settings)
    {
        _settings = settings;
        _startup.SetEnabled(settings.StartWithWindows);
        await _settingsStore.SaveAsync(settings);

        // Reconciles desired state (the setting) against actual state (is the subsystem running),
        // rather than diffing against a "previous settings" snapshot -- Settings is a live object the
        // settings window mutates in place, so a before/after value comparison here would always see
        // the same (already-mutated) instance on both sides.
        var bridgeRunning = _bridgeProcess is { HasExited: false };
        if (settings.CameraTrackingEnabled && !bridgeRunning) StartCameraTracking();
        else if (!settings.CameraTrackingEnabled && bridgeRunning) StopCameraTracking();

        var voiceRunning = _voice is not null;
        if (settings.VoiceCommandsEnabled && !voiceRunning) StartVoiceCommands();
        else if (!settings.VoiceCommandsEnabled && voiceRunning) StopVoiceCommands();
    }

    private async Task ToggleEnabledAsync()
    {
        _settings.Enabled = !_settings.Enabled;
        await _settingsStore.SaveAsync(_settings);
        _tray?.ShowBalloon("HeadPointer", _settings.Enabled ? "Tracking enabled" : "Tracking disabled");
    }

    private async Task PauseAsync()
    {
        _settings.Enabled = false;
        await _settingsStore.SaveAsync(_settings);
        _tray?.ShowBalloon("HeadPointer", "Tracking paused");
    }

    private void OnMonitorChanged(int monitorIndex)
    {
        if (!_settings.NotifyOnMonitorChange) return;
        var screen = _monitors.GetTarget(monitorIndex);
        _tray?.ShowBalloon("HeadPointer", $"Active monitor: {monitorIndex + 1} ({screen.Bounds.Width}x{screen.Bounds.Height})");
    }

    private void StartVoiceCommands()
    {
        if (_voice is not null) return;

        _voice = new VoiceCommandService(_logger);
        _voice.MonitorRequested += OnVoiceMonitorRequested;
        _voice.Start(_monitors.GetMonitors().Count, _settings.MicrophoneDeviceId);
    }

    private void StopVoiceCommands()
    {
        if (_voice is null) return;

        _voice.MonitorRequested -= OnVoiceMonitorRequested;
        _voice.Dispose();
        _voice = null;
    }

    private void OnVoiceMonitorRequested(int monitorIndex)
    {
        if (!_settings.Enabled)
        {
            _ = _logger.InfoAsync($"[voice] requested monitor {monitorIndex} but ignored: master 'Enable tracking' is off.");
            _tray?.ShowWarning("HeadPointer", "Voice command heard, but tracking is paused. Press Ctrl+Alt+H or check 'Enable tracking' in Settings.");
            return;
        }

        var monitors = _monitors.GetMonitors();
        if (monitorIndex < 0 || monitorIndex >= monitors.Count)
        {
            _ = _logger.InfoAsync($"[voice] requested monitor {monitorIndex} but only {monitors.Count} monitor(s) detected.");
            return;
        }

        if (_pointer is null)
        {
            _ = _logger.InfoAsync($"[voice] requested monitor {monitorIndex} but PointerService isn't ready yet.");
            return;
        }

        _ = _logger.InfoAsync($"[voice] moving to monitor {monitorIndex}.");
        _pointer.MoveToMonitor(monitorIndex, _settings.ActivateWindowOnMove, _settings.AllowSimulatedClickActivation);
        OnMonitorChanged(monitorIndex);
    }

    private void StartCameraTracking()
    {
        if (_bridgeProcess is { HasExited: false }) return;

        var exePath = FindBridgeExecutable();
        if (exePath is null)
        {
            _tray?.ShowWarning("HeadPointer", "Could not find CameraBridge\\HeadPointerMvp.CameraBridge.exe. Rebuild the solution.");
            return;
        }

        var arguments = $"--host {_settings.OpenSeeFaceHost} --port {_settings.OpenSeeFacePort} --camera {_settings.CameraIndex}";
        try
        {
            _bridgeProcess = Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(exePath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (_bridgeProcess is not null)
            {
                // Guarantees the bridge dies with this process even on a crash or force-kill, not
                // just on a clean Exit -- Windows does not kill child processes automatically.
                _bridgeJob.Assign(_bridgeProcess);
                _bridgeProcess.OutputDataReceived += (_, e) => { if (e.Data is not null) _ = _logger.InfoAsync($"[camera bridge] {e.Data}"); };
                _bridgeProcess.ErrorDataReceived += (_, e) => { if (e.Data is not null) _ = _logger.InfoAsync($"[camera bridge] {e.Data}"); };
                _bridgeProcess.BeginOutputReadLine();
                _bridgeProcess.BeginErrorReadLine();
            }
        }
        catch (Exception ex)
        {
            _tray?.ShowWarning("HeadPointer", $"Could not start camera tracking: {ex.Message}");
        }
    }

    private void StopCameraTracking()
    {
        try
        {
            if (_bridgeProcess is null || _bridgeProcess.HasExited) return;

            // The bridge runs without a console window (CreateNoWindow=true), so there is no
            // main window to close gracefully; the capture/detector loop is cheap to tear down
            // and the OS reclaims the camera handle on process exit, so a direct kill is safe.
            _bridgeProcess.Kill(entireProcessTree: true);
            _bridgeProcess.WaitForExit(1500);
            _bridgeProcess.Dispose();
            _bridgeProcess = null;
        }
        catch (Exception ex)
        {
            _tray?.ShowWarning("HeadPointer", $"Could not stop camera tracking: {ex.Message}");
        }
    }

    private static string? FindBridgeExecutable()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "CameraBridge", "HeadPointerMvp.CameraBridge.exe");
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }

        return null;
    }

    private void ShowSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(this);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private async void Exit()
    {
        await DisposeAsync();
        App.ExitApplication();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        StopCameraTracking();
        StopVoiceCommands();
        _bridgeJob.Dispose();
        _tray?.Dispose();
        _hotkeys?.Dispose();
        if (_poseSource is not null) await _poseSource.DisposeAsync();
        _stop.Dispose();
    }
}
