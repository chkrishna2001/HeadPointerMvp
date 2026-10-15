using System.Windows.Forms;
using HeadPointerMvp.Domain;
using HeadPointerMvp.Infrastructure;

namespace HeadPointerMvp.Services;

public sealed class HeadDirectionRouter
{
    private readonly Func<AppSettings> _settings;
    private readonly PointerService _pointer;
    private readonly MonitorService _monitors;
    private readonly FileLogger _logger;
    private float? _smoothedYaw;
    private int? _candidateTarget;
    private DateTimeOffset _candidateSince;
    private DateTimeOffset _lastMove;
    private int? _lastTarget;
    private bool _suppressMovement;
    private float? _pitchBaseline;
    private DateTimeOffset _holdUntil;

    /// <summary>
    /// When true, yaw is still smoothed and broadcast via <see cref="SmoothedYawChanged"/> (so the
    /// settings window's live readout keeps working) but the pointer is never moved. Used to stop
    /// the cursor jumping around the screen while the user is trying to calibrate.
    /// </summary>
    public bool SuppressMovement
    {
        get => _suppressMovement;
        set
        {
            if (_suppressMovement == value) return;
            _suppressMovement = value;
            _candidateTarget = null; // avoid an instant move on resume from a stale pre-pause candidate
        }
    }

    public HeadDirectionRouter(Func<AppSettings> settings, PointerService pointer, MonitorService monitors, FileLogger logger)
    {
        _settings = settings;
        _pointer = pointer;
        _monitors = monitors;
        _logger = logger;
    }

    /// <summary>Fires with every smoothed yaw sample, live pose data or not tracking is moving the pointer. Used by the settings window's calibration capture.</summary>
    public event Action<float>? SmoothedYawChanged;

    /// <summary>Fires with the target monitor index each time the router actually moves the pointer (after dwell/cooldown pass).</summary>
    public event Action<int>? MonitorChanged;

    /// <summary>
    /// Called when voice, a hotkey or the grid moved the pointer: records that monitor as current so head
    /// tracking doesn't "correct" it back, and holds head routing briefly so a stale head direction can't undo it.
    /// </summary>
    public void NotifyExternalMove(int monitorIndex)
    {
        _lastTarget = monitorIndex;
        _candidateTarget = null;
        _holdUntil = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(4);
    }

    public void Process(HeadPose pose)
    {
        var settings = _settings();
        if (!settings.Enabled || DateTimeOffset.UtcNow - pose.ObservedAt > TimeSpan.FromSeconds(1)) return;

        _smoothedYaw = _smoothedYaw is null
            ? pose.YawDegrees
            : _smoothedYaw.Value + (settings.SmoothingFactor * (pose.YawDegrees - _smoothedYaw.Value));
        SmoothedYawChanged?.Invoke(_smoothedYaw.Value);
        if (_suppressMovement) return;

        if (ShouldFreeze(pose, settings))
        {
            // Restart the dwell on resume so the first sample after looking back up can't trigger an instant jump.
            _candidateTarget = null;
            return;
        }

        var monitors = _monitors.GetMonitors();
        var target = ResolveTarget(_smoothedYaw.Value, settings, monitors, _lastTarget);

        if (_candidateTarget != target)
        {
            _candidateTarget = target;
            _candidateSince = pose.ObservedAt;
            return;
        }

        if (_lastTarget == target || pose.ObservedAt - _candidateSince < TimeSpan.FromMilliseconds(settings.DwellMilliseconds)
            || pose.ObservedAt - _lastMove < TimeSpan.FromMilliseconds(settings.CooldownMilliseconds)) return;

        _ = _logger.InfoAsync($"[camera] moving to monitor {target} (yaw {_smoothedYaw.Value:0.0}).");
        _pointer.MoveToMonitor(target, settings.ActivateWindowOnMove, settings.AllowSimulatedClickActivation);
        _lastTarget = target;
        _lastMove = pose.ObservedAt;
        MonitorChanged?.Invoke(target);
    }

    /// <summary>
    /// True while head movement should be ignored: just after an explicit move, while the user is typing or
    /// clicking (they are looking at the keys, not a monitor), or while the head is nodded away from its
    /// usual pitch (looking down at the keyboard).
    /// </summary>
    private bool ShouldFreeze(HeadPose pose, AppSettings settings)
    {
        if (DateTimeOffset.UtcNow < _holdUntil) return true;
        if (settings.FreezeHeadWhileTyping && IdleMilliseconds() < settings.TypingFreezeMilliseconds) return true;

        // Slow baseline of "head level" pitch, only learned while not frozen, so the freeze threshold
        // follows the user's own posture instead of a fixed number.
        if (_pitchBaseline is null)
        {
            _pitchBaseline = pose.PitchDegrees;
            return false;
        }

        if (Math.Abs(pose.PitchDegrees - _pitchBaseline.Value) > settings.PitchFreezeUnits) return true;
        _pitchBaseline += 0.02f * (pose.PitchDegrees - _pitchBaseline.Value);
        return false;
    }

    private static long IdleMilliseconds()
    {
        var info = new LastInputInfo { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return long.MaxValue;
        return unchecked((uint)Environment.TickCount - info.Time);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    /// <summary>
    /// Picks the monitor whose calibrated yaw is closest to the current reading once at least two
    /// monitors are calibrated. This is layout-agnostic: it doesn't assume monitors sit left-to-right
    /// in Windows' arrangement order, only that each one was captured while the user looked at it.
    /// Falls back to the fixed-threshold hop-from-center behavior when calibration is incomplete.
    /// </summary>
    private static int ResolveTarget(float yaw, AppSettings settings, IReadOnlyList<Screen> monitors, int? currentMonitor)
    {
        var calibrated = CalibratedTargets(settings, monitors);
        if (calibrated.Count >= 2)
        {
            var best = calibrated[0];
            var bestDistance = Math.Abs(best.Yaw - yaw);
            for (var i = 1; i < calibrated.Count; i++)
            {
                var distance = Math.Abs(calibrated[i].Yaw - yaw);
                if (distance < bestDistance)
                {
                    best = calibrated[i];
                    bestDistance = distance;
                }
            }

            // Hysteresis: stay on the current monitor unless the head is clearly past the midpoint to the
            // new one (35%/65% instead of 50%/50%), so jitter around a boundary can't flip-flop.
            foreach (var current in calibrated)
            {
                if (current.Index != currentMonitor || current.Index == best.Index) continue;
                var gap = Math.Abs(current.Yaw - best.Yaw);
                if (Math.Abs(current.Yaw - yaw) <= bestDistance + (0.3f * gap)) return current.Index;
            }

            return best.Index;
        }

        var direction = Classify(yaw, settings.YawThresholdDegrees);
        return TargetIndex(direction, settings.CenterMonitorIndex, monitors.Count);
    }

    private static List<(int Index, float Yaw)> CalibratedTargets(AppSettings settings, IReadOnlyList<Screen> monitors)
    {
        var result = new List<(int Index, float Yaw)>();
        for (var i = 0; i < monitors.Count; i++)
        {
            if (settings.MonitorYawCalibration.TryGetValue(monitors[i].DeviceName, out var yaw)) result.Add((i, yaw));
        }

        return result;
    }

    private static Direction Classify(float yaw, float threshold) => yaw switch
    {
        var value when value <= -threshold => Direction.Left,
        var value when value >= threshold => Direction.Right,
        _ => Direction.Center
    };

    private static int TargetIndex(Direction direction, int center, int count) => direction switch
    {
        Direction.Left => Math.Max(0, center - 1),
        Direction.Right => Math.Min(count - 1, center + 1),
        _ => Math.Clamp(center, 0, count - 1)
    };
}
