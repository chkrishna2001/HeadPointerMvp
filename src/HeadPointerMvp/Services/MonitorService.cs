using System.Windows.Forms;

namespace HeadPointerMvp.Services;

public sealed class MonitorService
{
    public IReadOnlyList<Screen> GetMonitors() => Screen.AllScreens.OrderBy(screen => screen.Bounds.Left).ToArray();

    public Screen GetTarget(int requestedIndex)
    {
        var monitors = GetMonitors();
        if (monitors.Count == 0) throw new InvalidOperationException("No monitors found.");
        return monitors[Math.Clamp(requestedIndex, 0, monitors.Count - 1)];
    }
}
