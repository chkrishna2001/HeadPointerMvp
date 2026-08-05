using System.Drawing;
using System.Windows.Forms;

namespace HeadPointerMvp.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayIconService(Action showSettings, Action toggle, Action exit)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Settings…", null, (_, _) => showSettings());
        menu.Items.Add("Enable / Disable", null, (_, _) => toggle());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());
        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "HeadPointer MVP",
            Visible = true,
            ContextMenuStrip = menu
        };
        _icon.DoubleClick += (_, _) => showSettings();
    }

    public void ShowBalloon(string title, string text) => _icon.ShowBalloonTip(1800, title, text, ToolTipIcon.Info);

    public void ShowWarning(string title, string text) => _icon.ShowBalloonTip(3500, title, text, ToolTipIcon.Warning);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
