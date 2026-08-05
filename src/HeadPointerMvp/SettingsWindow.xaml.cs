using HeadPointerMvp.Domain;
using HeadPointerMvp.Services;
using System.Drawing;
using System.Windows.Forms;

namespace HeadPointerMvp;

public sealed class SettingsWindow : Form
{
    private readonly AppHost _host;
    private readonly CheckBox _enabledSwitch = new() { Text = "Enable tracking -- MUST be on for camera or voice to move the pointer (Ctrl+Alt+P also toggles this)", AutoSize = true };
    private readonly CheckBox _cameraTrackingSwitch = new() { Text = "Camera tracking (webcam head-pose)", AutoSize = true };
    private readonly CheckBox _voiceCommandsSwitch = new() { Text = "Voice commands (say \"monitor one\", \"monitor two\", ...)", AutoSize = true };
    private readonly ComboBox _microphoneBox = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly List<(string Id, string Name)> _microphones = new();
    private readonly NumericUpDown _dwellBox = new() { Minimum = 100, Maximum = 3000, Increment = 50 };
    private readonly NumericUpDown _cooldownBox = new() { Minimum = 250, Maximum = 5000, Increment = 50 };
    private readonly NumericUpDown _thresholdBox = new() { Minimum = 3, Maximum = 45, DecimalPlaces = 1, Increment = 1 };
    private readonly NumericUpDown _smoothingBox = new() { Minimum = 0.05m, Maximum = 1, DecimalPlaces = 2, Increment = 0.05m };
    private readonly NumericUpDown _centerMonitorBox = new() { Minimum = 0, Maximum = 10 };
    private readonly CheckBox _startupSwitch = new() { Text = "Start with Windows", AutoSize = true };
    private readonly CheckBox _activateWindowSwitch = new() { Text = "Activate window under pointer after a move", AutoSize = true };
    private readonly CheckBox _allowClickActivationSwitch = new() { Text = "  Allow a simulated click as an activation fallback", AutoSize = true };
    private readonly CheckBox _notifyOnMoveSwitch = new() { Text = "Show a notification when the active monitor changes", AutoSize = true };
    private readonly TextBox _hostBox = new();
    private readonly NumericUpDown _portBox = new() { Minimum = 1024, Maximum = 65535 };
    private readonly NumericUpDown _cameraIndexBox = new() { Minimum = 0, Maximum = 10 };
    private readonly Label _statusText = new() { AutoSize = true, ForeColor = Color.DarkRed };
    private readonly Label _liveYawLabel = new() { AutoSize = true, ForeColor = Color.DimGray };
    private readonly Dictionary<string, float> _calibration = new();
    private readonly List<(string DeviceName, Label ValueLabel)> _calibrationRows = new();
    private readonly System.Windows.Forms.Timer _yawTimer = new() { Interval = 200 };

    public SettingsWindow(AppHost host)
    {
        _host = host;
        BuildUi();
        LoadSettings(_host.Settings);
        _yawTimer.Tick += (_, _) => UpdateLiveYaw();
        _yawTimer.Start();

        // The pointer would otherwise keep jumping to whatever monitor tracking currently resolves
        // to while the user is trying to look at each screen and click Capture.
        _host.SetCalibrationPause(true);
        FormClosed += (_, _) =>
        {
            _yawTimer.Stop();
            _host.SetCalibrationPause(false);
        };
    }

    private void LoadSettings(AppSettings settings)
    {
        _enabledSwitch.Checked = settings.Enabled;
        _cameraTrackingSwitch.Checked = settings.CameraTrackingEnabled;
        _voiceCommandsSwitch.Checked = settings.VoiceCommandsEnabled;
        var micIndex = _microphones.FindIndex(m => m.Id == settings.MicrophoneDeviceId);
        _microphoneBox.SelectedIndex = micIndex >= 0 ? micIndex : 0;
        _dwellBox.Value = settings.DwellMilliseconds;
        _cooldownBox.Value = settings.CooldownMilliseconds;
        _thresholdBox.Value = (decimal)settings.YawThresholdDegrees;
        _smoothingBox.Value = (decimal)settings.SmoothingFactor;
        _centerMonitorBox.Value = settings.CenterMonitorIndex;
        _startupSwitch.Checked = settings.StartWithWindows;
        _activateWindowSwitch.Checked = settings.ActivateWindowOnMove;
        _allowClickActivationSwitch.Checked = settings.AllowSimulatedClickActivation;
        _notifyOnMoveSwitch.Checked = settings.NotifyOnMonitorChange;
        _hostBox.Text = settings.OpenSeeFaceHost;
        _portBox.Value = settings.OpenSeeFacePort;
        _cameraIndexBox.Value = settings.CameraIndex;

        _calibration.Clear();
        foreach (var (deviceName, yaw) in settings.MonitorYawCalibration) _calibration[deviceName] = yaw;
        foreach (var (deviceName, valueLabel) in _calibrationRows) UpdateCalibrationLabel(valueLabel, deviceName);
    }

    private async void Save_Click(object? sender, EventArgs e)
    {
        try
        {
            await _host.SaveSettingsAsync(BuildSettingsFromControls());
            Close();
        }
        catch (Exception ex)
        {
            _statusText.Text = ex.Message;
        }
    }

    /// <summary>Reads every control back into <see cref="AppHost.Settings"/> (the live settings object).</summary>
    private AppSettings BuildSettingsFromControls()
    {
        var settings = _host.Settings;
        settings.Enabled = _enabledSwitch.Checked;
        settings.CameraTrackingEnabled = _cameraTrackingSwitch.Checked;
        settings.VoiceCommandsEnabled = _voiceCommandsSwitch.Checked;
        settings.MicrophoneDeviceId = _microphoneBox.SelectedIndex > 0 ? _microphones[_microphoneBox.SelectedIndex].Id : string.Empty;
        settings.DwellMilliseconds = (int)_dwellBox.Value;
        settings.CooldownMilliseconds = (int)_cooldownBox.Value;
        settings.YawThresholdDegrees = (float)_thresholdBox.Value;
        settings.SmoothingFactor = (float)_smoothingBox.Value;
        settings.CenterMonitorIndex = (int)_centerMonitorBox.Value;
        settings.StartWithWindows = _startupSwitch.Checked;
        settings.ActivateWindowOnMove = _activateWindowSwitch.Checked;
        settings.AllowSimulatedClickActivation = _allowClickActivationSwitch.Checked;
        settings.NotifyOnMonitorChange = _notifyOnMoveSwitch.Checked;
        settings.OpenSeeFaceHost = _hostBox.Text.Trim();
        settings.OpenSeeFacePort = (int)_portBox.Value;
        settings.CameraIndex = (int)_cameraIndexBox.Value;
        settings.MonitorYawCalibration = new Dictionary<string, float>(_calibration);
        return settings;
    }

    private void Cancel_Click(object? sender, EventArgs e) => Close();

    private void UpdateLiveYaw() =>
        _liveYawLabel.Text = _host.TryGetLatestYaw(out var yaw)
            ? $"Live yaw: {yaw:0.0}°"
            : "Live yaw: no reading yet (enable tracking + start the camera bridge)";

    private void UpdateCalibrationLabel(Label label, string deviceName) =>
        label.Text = _calibration.TryGetValue(deviceName, out var yaw) ? $"{yaw:0.0}°" : "not set";

    private void PopulateMicrophones()
    {
        _microphones.Clear();
        _microphones.Add((string.Empty, "System default"));
        _microphones.AddRange(VoiceCommandService.ListMicrophones());

        _microphoneBox.Items.Clear();
        foreach (var (_, name) in _microphones) _microphoneBox.Items.Add(name);
        if (_microphoneBox.Items.Count > 0) _microphoneBox.SelectedIndex = 0;
    }

    private void BuildUi()
    {
        Text = "HeadPointer settings";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(480, 800);
        Size = new Size(480, 800);

        PopulateMicrophones();

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 2,
            RowCount = 21
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));

        var title = new Label
        {
            Text = "HeadPointer MVP",
            AutoSize = true,
            Font = new Font(Font.FontFamily, 13, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 12)
        };
        panel.Controls.Add(title, 0, 0);
        panel.SetColumnSpan(title, 2);
        panel.Controls.Add(_enabledSwitch, 0, 1);
        panel.SetColumnSpan(_enabledSwitch, 2);
        panel.Controls.Add(_cameraTrackingSwitch, 0, 2);
        panel.SetColumnSpan(_cameraTrackingSwitch, 2);
        panel.Controls.Add(_voiceCommandsSwitch, 0, 3);
        panel.SetColumnSpan(_voiceCommandsSwitch, 2);
        AddRow(panel, 4, "Microphone", _microphoneBox);
        AddRow(panel, 5, "Dwell time (ms)", _dwellBox);
        AddRow(panel, 6, "Cooldown (ms)", _cooldownBox);
        AddRow(panel, 7, "Yaw threshold", _thresholdBox);
        AddRow(panel, 8, "Smoothing", _smoothingBox);
        AddRow(panel, 9, "Center monitor index", _centerMonitorBox);
        panel.Controls.Add(_startupSwitch, 0, 10);
        panel.SetColumnSpan(_startupSwitch, 2);
        panel.Controls.Add(_activateWindowSwitch, 0, 11);
        panel.SetColumnSpan(_activateWindowSwitch, 2);
        panel.Controls.Add(_allowClickActivationSwitch, 0, 12);
        panel.SetColumnSpan(_allowClickActivationSwitch, 2);
        panel.Controls.Add(_notifyOnMoveSwitch, 0, 13);
        panel.SetColumnSpan(_notifyOnMoveSwitch, 2);
        AddRow(panel, 14, "Bridge host", _hostBox);
        AddRow(panel, 15, "Bridge UDP port", _portBox);
        AddRow(panel, 16, "Camera index", _cameraIndexBox);

        var calibrationPanel = BuildCalibrationPanel();
        panel.Controls.Add(calibrationPanel, 0, 17);
        panel.SetColumnSpan(calibrationPanel, 2);

        var hint = new Label
        {
            Text = "Hotkeys: Ctrl+Alt+H toggles tracking; Ctrl+Alt+P pauses it. Changing the microphone requires toggling Voice commands off and back on to take effect.",
            AutoSize = false,
            Width = 400,
            MaximumSize = new Size(400, 0),
            ForeColor = Color.DimGray
        };
        panel.Controls.Add(hint, 0, 18);
        panel.SetColumnSpan(hint, 2);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        var save = new Button { Text = "Save", AutoSize = true };
        var cancel = new Button { Text = "Cancel", AutoSize = true };
        save.Click += Save_Click;
        cancel.Click += Cancel_Click;
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        panel.Controls.Add(buttons, 0, 19);
        panel.SetColumnSpan(buttons, 2);
        panel.Controls.Add(_statusText, 0, 20);
        panel.SetColumnSpan(_statusText, 2);

        Controls.Add(panel);
    }

    /// <summary>
    /// One row per detected monitor: look at that screen, click Capture to record the current live
    /// yaw against it. Once two or more monitors are captured, HeadDirectionRouter routes by nearest
    /// calibrated yaw instead of hopping left/right from the center monitor index, so it no longer
    /// matters whether Windows' display arrangement matches the physical desk layout.
    ///
    /// Capture/Clear save immediately (not deferred to the form's Save button) so calibration is
    /// never lost by closing the window without remembering to press Save.
    /// </summary>
    private GroupBox BuildCalibrationPanel()
    {
        var group = new GroupBox { Text = "Camera-tracking calibration", Dock = DockStyle.Fill, AutoSize = true };
        var layout = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Dock = DockStyle.Top,
            Padding = new Padding(0, 18, 0, 0)
        };

        layout.Controls.Add(new Label
        {
            Text = "Look at a monitor, then click Capture (saved immediately). Calibrate at least two monitors to enable layout-aware tracking. Pointer movement is paused while this window is open.",
            AutoSize = false,
            Width = 400,
            MaximumSize = new Size(400, 0)
        });
        layout.Controls.Add(_liveYawLabel);

        var monitors = _host.Monitors.GetMonitors();
        for (var i = 0; i < monitors.Count; i++)
        {
            var screen = monitors[i];
            var deviceName = screen.DeviceName;
            var row = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false };
            // 1-indexed display ("Monitor 1", "Monitor 2", ...) to match how voice commands are
            // spoken ("monitor one" = your first monitor); the underlying index (i) stays 0-based.
            var label = new Label { Text = $"Monitor {i + 1} ({screen.Bounds.Width}x{screen.Bounds.Height})", AutoSize = true, Width = 170, Margin = new Padding(0, 6, 8, 0) };
            var valueLabel = new Label { AutoSize = true, Width = 60, Margin = new Padding(0, 6, 8, 0) };
            var captureButton = new Button { Text = "Capture", AutoSize = true };
            var clearButton = new Button { Text = "Clear", AutoSize = true };

            captureButton.Click += async (_, _) =>
            {
                if (_host.TryGetLatestYaw(out var yaw))
                {
                    _calibration[deviceName] = yaw;
                    UpdateCalibrationLabel(valueLabel, deviceName);
                    await SaveCalibrationAsync();
                }
                else
                {
                    _statusText.Text = "No live head-pose reading yet. Start camera tracking first.";
                }
            };
            clearButton.Click += async (_, _) =>
            {
                _calibration.Remove(deviceName);
                UpdateCalibrationLabel(valueLabel, deviceName);
                await SaveCalibrationAsync();
            };

            UpdateCalibrationLabel(valueLabel, deviceName);
            _calibrationRows.Add((deviceName, valueLabel));

            row.Controls.Add(label);
            row.Controls.Add(valueLabel);
            row.Controls.Add(captureButton);
            row.Controls.Add(clearButton);
            layout.Controls.Add(row);
        }

        group.Controls.Add(layout);
        return group;
    }

    private async Task SaveCalibrationAsync()
    {
        try
        {
            _host.Settings.MonitorYawCalibration = new Dictionary<string, float>(_calibration);
            await _host.SaveSettingsAsync(_host.Settings);
            _statusText.ForeColor = Color.DarkGreen;
            _statusText.Text = "Calibration saved.";
        }
        catch (Exception ex)
        {
            _statusText.ForeColor = Color.DarkRed;
            _statusText.Text = ex.Message;
        }
    }

    private static void AddRow(TableLayoutPanel panel, int row, string label, Control control)
    {
        control.Dock = DockStyle.Fill;
        panel.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        panel.Controls.Add(control, 1, row);
    }
}
