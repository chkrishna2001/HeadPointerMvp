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
    private GridPointerService? _grid;
    private TriggerHookService? _triggers;
    private HeadTiltGesture? _tilt;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
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

        _grid = new GridPointerService(_pointer, _monitors, _logger);
        _grid.ActiveChanged += active => _voice?.SetGridMode(active);

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
            _poseSource.PoseReceived += (_, pose) =>
            {
                router.Process(pose);
                _tilt?.Process(pose);
            };
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
        _hotkeys = new HotKeyService();
        TryRegister(_settings.ToggleHotkey, () => _ = ToggleEnabledAsync());
        TryRegister(_settings.PauseHotkey, () => _ = PauseAsync());
        TryRegister(_settings.GridHotkey, () => _grid?.Toggle());

        if (_settings.MonitorHotkeysEnabled)
        {
            var count = Math.Min(_monitors.GetMonitors().Count, 9);
            for (var i = 0; i < count; i++)
            {
                var index = i;
                TryRegister($"Ctrl+Alt+D{i + 1}", () => MoveToMonitorExplicit(index, "hotkey"));
            }
        }

        // Low-level hooks as well: these still fire when a full-screen remote desktop has focus and
        // would otherwise swallow the chords. They run first and swallow the keys; RegisterHotKey above stays as a fallback.
        _triggers = new TriggerHookService(_logger, OnUi);
        _triggers.AddChord(_settings.GridHotkey, () => _grid?.Toggle());
        if (_settings.MonitorHotkeysEnabled)
        {
            var count = Math.Min(_monitors.GetMonitors().Count, 9);
            for (var i = 0; i < count; i++)
            {
                var index = i;
                _triggers.AddChord($"Ctrl+Alt+D{i + 1}", () => MoveToMonitorExplicit(index, "hotkey"));
            }
        }

        _triggers.SetMouseButton(_settings.GridMouseButton, () => _grid?.Toggle());
        _triggers.Install();

        _tilt = new HeadTiltGesture(() => _settings, () => OnUi(() =>
        {
            _ = _logger.InfoAsync("[trigger] head tilt held -> grid.");
            _grid?.Toggle();
        }));

        await Task.CompletedTask;
    }

    // One clashing combo must not take the others down with it (it used to abort all registration).
    private void TryRegister(string shortcut, Action action)
    {
        try
        {
            _hotkeys!.Register(shortcut, action);
            _ = _logger.InfoAsync($"Hotkey {shortcut} registered.");
        }
        catch (Exception ex)
        {
            _ = _logger.InfoAsync($"Hotkey {shortcut} unavailable: {ex.Message}");
            _tray?.ShowWarning("HeadPointer hotkey unavailable", $"{shortcut}: {ex.Message}");
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
        _voice.IntentRaised += intent => OnUi(() => HandleVoiceIntent(intent));
        _voice.Feedback += (kind, text) => VoiceFeedback(kind, text);
        _voice.GridDigitSpoken += digit => OnUi(() => _grid?.Zoom(digit));
        _voice.GridCommandSpoken += command => OnUi(() => _grid?.Execute(command));
        _voice.Start(_monitors.GetMonitors().Count, _settings.MicrophoneDeviceId, _settings.VoiceWakeWord, _settings.VoiceMacros);
    }

    /// <summary>Tells the user what the voice engine did: rising chirp = "listening", double chirp = command ran, low buzz = no command heard.</summary>
    private void VoiceFeedback(string kind, string text)
    {
        _ = _logger.InfoAsync($"[voice] feedback: {kind} {text}".TrimEnd());
        var tones = kind switch
        {
            "wake" => new[] { (900, 90) },
            "ok" => new[] { (700, 70), (1100, 90) },
            "miss" => new[] { (220, 250) },
            _ => Array.Empty<(int, int)>()
        };
        _ = Task.Run(() => PlayTones(tones));
    }

    /// <summary>Plays beeps through the default output device (Console.Beep is silent for a tray app on many setups).</summary>
    private void PlayTones((int Hz, int Ms)[] tones)
    {
        try
        {
            foreach (var (hz, ms) in tones)
            {
                var tone = new NAudio.Wave.SampleProviders.OffsetSampleProvider(
                    new NAudio.Wave.SampleProviders.SignalGenerator(44100, 1) { Type = NAudio.Wave.SampleProviders.SignalGeneratorType.Sin, Frequency = hz, Gain = 0.3 })
                { Take = TimeSpan.FromMilliseconds(ms) };
                using var output = new NAudio.Wave.WaveOutEvent();
                output.Init(tone);
                output.Play();
                while (output.PlaybackState == NAudio.Wave.PlaybackState.Playing) Thread.Sleep(10);
            }
        }
        catch (Exception ex)
        {
            _ = _logger.InfoAsync($"[voice] feedback sound failed: {ex.Message}");
        }
    }

    private void StopVoiceCommands()
    {
        if (_voice is null) return;

        _voice.Dispose();
        _voice = null;
    }

    private void HandleVoiceIntent(VoiceIntent intent)
    {
        if (_pointer is null) return;
        switch (intent.Kind)
        {
            case VoiceIntentKind.Grid:
                _grid?.Begin();
                break;
            case VoiceIntentKind.GridClose:
                _grid?.Close();
                break;
            case VoiceIntentKind.Monitor:
                MoveToMonitorExplicit(intent.Index, "voice");
                break;
            case VoiceIntentKind.Press:
                _ = _logger.InfoAsync($"[voice] pressing {intent.Chord}.");
                _pointer.SendChord(intent.Chord);
                break;
            case VoiceIntentKind.Click:
                _pointer.ClickHere(PointerService.ClickKind.Left);
                break;
            case VoiceIntentKind.RightClick:
                _pointer.ClickHere(PointerService.ClickKind.Right);
                break;
            case VoiceIntentKind.DoubleClick:
                _pointer.ClickHere(PointerService.ClickKind.Double);
                break;
            case VoiceIntentKind.ScrollUp:
                _pointer.Scroll(5);
                break;
            case VoiceIntentKind.ScrollDown:
                _pointer.Scroll(-5);
                break;
        }
    }

    /// <summary>Speech events arrive on the recognizer's own thread; windows and hooks must be touched on the UI thread.</summary>
    private void OnUi(Action action)
    {
        if (_ui is null) action();
        else _ui.Post(_ => action(), null);
    }

    /// <summary>
    /// Explicit user commands (voice, Ctrl+Alt+digit) always work, even when head tracking is paused --
    /// 'Enable tracking' only governs the head-driven moves.
    /// </summary>
    private void MoveToMonitorExplicit(int monitorIndex, string source)
    {
        var monitors = _monitors.GetMonitors();
        if (monitorIndex < 0 || monitorIndex >= monitors.Count)
        {
            _ = _logger.InfoAsync($"[{source}] requested monitor {monitorIndex} but only {monitors.Count} monitor(s) detected.");
            return;
        }

        if (_pointer is null) return;

        _ = _logger.InfoAsync($"[{source}] moving to monitor {monitorIndex}.");
        _grid?.Close();
        _pointer.MoveToMonitor(monitorIndex, _settings.ActivateWindowOnMove, _settings.AllowSimulatedClickActivation);
        _router?.NotifyExternalMove(monitorIndex);
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
        _grid?.Dispose();
        _triggers?.Dispose();
        _bridgeJob.Dispose();
        _tray?.Dispose();
        _hotkeys?.Dispose();
        if (_poseSource is not null) await _poseSource.DisposeAsync();
        _stop.Dispose();
    }
}
