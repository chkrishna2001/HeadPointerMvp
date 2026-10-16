using HeadPointerMvp.Domain;

namespace HeadPointerMvp.Services;

/// <summary>
/// Deliberate head tilt (ear toward shoulder), held, as a keyboard-free trigger. The camera path never
/// goes through the focused app, so this works even with a full-screen remote desktop in front.
/// Fires once per tilt; re-arms only after the head is back near level.
/// </summary>
public sealed class HeadTiltGesture(Func<AppSettings> settings, Action triggered)
{
    private static readonly TimeSpan MinGap = TimeSpan.FromMilliseconds(1500);

    private float? _smoothed;
    private float? _baseline;
    private DateTimeOffset? _tiltSince;
    private DateTimeOffset _lastFired;
    private bool _armed = true;

    public void Process(HeadPose pose)
    {
        var s = settings();
        if (!s.HeadTiltOpensGrid) return;

        // Roll from eye landmarks is a little noisy; smooth it, and follow slow posture drift via a baseline.
        _smoothed = _smoothed is null ? pose.RollDegrees : _smoothed + (0.3f * (pose.RollDegrees - _smoothed));
        _baseline ??= _smoothed;
        var deviation = Math.Abs(_smoothed.Value - _baseline.Value);

        if (deviation < s.TiltDegrees * 0.5f)
        {
            _baseline += 0.02f * (_smoothed.Value - _baseline.Value);
            _armed = true;
            _tiltSince = null;
            return;
        }

        if (!_armed || deviation < s.TiltDegrees)
        {
            _tiltSince = null;
            return;
        }

        _tiltSince ??= pose.ObservedAt;
        if (pose.ObservedAt - _tiltSince < TimeSpan.FromMilliseconds(s.TiltHoldMilliseconds)) return;
        if (pose.ObservedAt - _lastFired < MinGap) return;

        _armed = false;
        _tiltSince = null;
        _lastFired = pose.ObservedAt;
        triggered();
    }
}
