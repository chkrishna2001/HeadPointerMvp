using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using HeadPointerMvp.Infrastructure;

namespace HeadPointerMvp.Services;

public enum GridCommand
{
    Click,
    RightClick,
    DoubleClick,
    Back,
    Cancel,
    NextMonitor
}

/// <summary>
/// Hands-free pointing: a 3x3 numbered grid over the monitor. Each digit zooms the pointer into that
/// cell (the pointer follows, so you see where you are) and redraws the grid inside it, so four or five
/// digits land within a few pixels of anywhere. Enter clicks. Driven by keyboard (a low-level hook that
/// only exists while the grid is open) or by voice ("one".."nine", "click", "back", "cancel").
/// Must be created and used on the UI thread.
/// </summary>
public sealed class GridPointerService(PointerService pointer, MonitorService monitors, FileLogger logger) : IDisposable
{
    private const int Divisions = 3;
    private const int MinCellPixels = 6;

    private readonly Stack<Rectangle> _history = new();
    private readonly HashSet<int> _swallowedKeys = [];
    private GridForm? _form;
    private Rectangle _rect;
    private int _monitorIndex;
    private IntPtr _hook;
    private LowLevelKeyboardProc? _hookProc; // kept in a field so the GC can't collect the delegate the OS is calling

    public bool IsActive => _form is not null;

    public event Action<bool>? ActiveChanged;

    /// <summary>Opens the grid on the monitor under the cursor, or closes it if already open.</summary>
    public void Toggle()
    {
        if (IsActive) Close();
        else Begin();
    }

    public void Begin()
    {
        if (IsActive) return;

        var all = monitors.GetMonitors();
        var cursor = Cursor.Position;
        _monitorIndex = Math.Max(0, all.ToList().FindIndex(screen => screen.Bounds.Contains(cursor)));
        ShowOnMonitor(all[_monitorIndex]);

        _hookProc = HookCallback;
        _hook = SetWindowsHookEx(WhKeyboardLl, _hookProc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) _ = logger.InfoAsync($"[grid] keyboard hook failed (Win32 error {Marshal.GetLastWin32Error()}); voice still works.");
        ActiveChanged?.Invoke(true);
        _ = logger.InfoAsync("[grid] opened.");
    }

    public void Close()
    {
        if (!IsActive) return;
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _hookProc = null;
        _swallowedKeys.Clear();
        _form?.Close();
        _form?.Dispose();
        _form = null;
        _history.Clear();
        ActiveChanged?.Invoke(false);
    }

    /// <summary>Zooms into cell 1..9 (reading order).</summary>
    public void Zoom(int cell)
    {
        if (!IsActive || cell is < 1 or > Divisions * Divisions) return;
        var cellWidth = _rect.Width / (double)Divisions;
        var cellHeight = _rect.Height / (double)Divisions;
        if (cellWidth < MinCellPixels || cellHeight < MinCellPixels) return; // already as fine as it gets: Enter to click

        var column = (cell - 1) % Divisions;
        var row = (cell - 1) / Divisions;
        _history.Push(_rect);
        _rect = new Rectangle(
            _rect.Left + (int)Math.Round(column * cellWidth),
            _rect.Top + (int)Math.Round(row * cellHeight),
            Math.Max(1, (int)Math.Round(cellWidth)),
            Math.Max(1, (int)Math.Round(cellHeight)));
        Refresh();
    }

    public void Execute(GridCommand command)
    {
        if (!IsActive) return;
        switch (command)
        {
            case GridCommand.Click:
                ClickAndClose(PointerService.ClickKind.Left);
                break;
            case GridCommand.RightClick:
                ClickAndClose(PointerService.ClickKind.Right);
                break;
            case GridCommand.DoubleClick:
                ClickAndClose(PointerService.ClickKind.Double);
                break;
            case GridCommand.Back:
                if (_history.Count > 0)
                {
                    _rect = _history.Pop();
                    Refresh();
                }

                break;
            case GridCommand.Cancel:
                Close();
                break;
            case GridCommand.NextMonitor:
                var all = monitors.GetMonitors();
                _monitorIndex = (_monitorIndex + 1) % all.Count;
                _history.Clear();
                ShowOnMonitor(all[_monitorIndex]);
                break;
        }
    }

    private void ClickAndClose(PointerService.ClickKind kind)
    {
        var x = _rect.Left + (_rect.Width / 2);
        var y = _rect.Top + (_rect.Height / 2);
        Close();
        // Overlay is gone before the click so it can never be the thing that receives it.
        pointer.ClickAt(x, y, kind);
        _ = logger.InfoAsync($"[grid] {kind} click at {x},{y}.");
    }

    private void ShowOnMonitor(Screen screen)
    {
        _rect = screen.Bounds;
        _form?.Close();
        _form?.Dispose();
        _form = new GridForm(screen.Bounds);
        _form.Show();
        Refresh();
    }

    private void Refresh()
    {
        if (_form is null) return;
        pointer.MoveTo(_rect.Left + (_rect.Width / 2), _rect.Top + (_rect.Height / 2));
        _form.SetRegion(_rect, Divisions, _history.Count);
    }

    // ---- keyboard ------------------------------------------------------------------------------

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var vk = Marshal.ReadInt32(lParam);
            var message = wParam.ToInt32();
            var isDown = message is WmKeyDown or WmSysKeyDown;

            if (!isDown && _swallowedKeys.Remove(vk)) return 1; // swallow the matching key-up too

            // Leave anything with Ctrl/Alt/Win held alone so the app's own hotkeys keep working.
            if (isDown && !ModifierHeld() && TryMap(vk, out var action))
            {
                _swallowedKeys.Add(vk);
                // Handle after the hook returns: touching windows and SendInput from inside a hook callback is fragile.
                _form?.BeginInvoke(action);
                return 1;
            }
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private bool TryMap(int vk, out Action action)
    {
        action = () => { };
        switch (vk)
        {
            case >= 0x31 and <= 0x39: // top-row 1-9
                var topRow = vk - 0x30;
                action = () => Zoom(topRow);
                return true;
            case >= 0x61 and <= 0x69: // numpad 1-9 (the digit shown on screen, not the physical position)
                var numpad = vk - 0x60;
                action = () => Zoom(numpad);
                return true;
            case 0x0D or 0x20: // Enter, Space
                action = () => Execute(GridCommand.Click);
                return true;
            case 0x52: // R
                action = () => Execute(GridCommand.RightClick);
                return true;
            case 0x44: // D
                action = () => Execute(GridCommand.DoubleClick);
                return true;
            case 0x08 or 0x5A: // Backspace, Z
                action = () => Execute(GridCommand.Back);
                return true;
            case 0x09: // Tab
                action = () => Execute(GridCommand.NextMonitor);
                return true;
            case 0x1B: // Esc
                action = () => Execute(GridCommand.Cancel);
                return true;
            default:
                return false;
        }
    }

    private static bool ModifierHeld() =>
        (GetKeyState(0x11) & 0x8000) != 0 || (GetKeyState(0x12) & 0x8000) != 0 || (GetKeyState(0x5B) & 0x8000) != 0 || (GetKeyState(0x5C) & 0x8000) != 0;

    public void Dispose() => Close();

    // ---- overlay window ------------------------------------------------------------------------

    private sealed class GridForm : Form
    {
        // Pixels of this exact colour are see-through; everything we draw is a solid, different colour.
        private static readonly Color KeyColor = Color.FromArgb(1, 2, 3);
        private Rectangle _region;
        private int _divisions = 3;
        private int _depth;

        public GridForm(Rectangle bounds)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = KeyColor;
            TransparencyKey = KeyColor;
            DoubleBuffered = true;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                // No-activate + tool window + click-through: never steals focus from what you are typing into.
                cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00000020;
                return cp;
            }
        }

        public void SetRegion(Rectangle screenRegion, int divisions, int depth)
        {
            _region = new Rectangle(screenRegion.Left - Left, screenRegion.Top - Top, screenRegion.Width, screenRegion.Height);
            _divisions = divisions;
            _depth = depth;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.None;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            // Colour shifts with depth so a new level is obvious at a glance.
            var accent = _depth switch { 0 => Color.FromArgb(255, 200, 0), 1 => Color.FromArgb(0, 220, 120), 2 => Color.FromArgb(0, 190, 255), _ => Color.FromArgb(255, 90, 200) };
            using var border = new Pen(accent, 4);
            using var line = new Pen(accent, 2);
            g.DrawRectangle(border, _region.Left, _region.Top, _region.Width - 1, _region.Height - 1);

            var cellW = _region.Width / (float)_divisions;
            var cellH = _region.Height / (float)_divisions;
            for (var i = 1; i < _divisions; i++)
            {
                g.DrawLine(line, _region.Left + (cellW * i), _region.Top, _region.Left + (cellW * i), _region.Bottom);
                g.DrawLine(line, _region.Left, _region.Top + (cellH * i), _region.Right, _region.Top + (cellH * i));
            }

            var fontSize = Math.Clamp(Math.Min(cellW, cellH) / 3.2f, 8f, 44f);
            using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            using var labelBack = new SolidBrush(Color.FromArgb(24, 24, 28));
            using var labelText = new SolidBrush(accent);
            for (var row = 0; row < _divisions; row++)
            {
                for (var column = 0; column < _divisions; column++)
                {
                    var text = ((row * _divisions) + column + 1).ToString();
                    var size = g.MeasureString(text, font);
                    var boxW = size.Width + 6;
                    var boxH = size.Height;
                    var cx = _region.Left + (cellW * (column + 0.5f));
                    var cy = _region.Top + (cellH * (row + 0.5f));
                    // Skip a label that would cover more than a third of a tiny cell; the lines still show where to zoom.
                    if (boxW > cellW * 0.8f || boxH > cellH * 0.8f) continue;
                    g.FillRectangle(labelBack, cx - (boxW / 2), cy - (boxH / 2), boxW, boxH);
                    g.DrawString(text, font, labelText, cx - (size.Width / 2), cy - (size.Height / 2));
                }
            }

            // Hint strip, bottom-centre of the screen.
            using var hintFont = new Font("Segoe UI", 15, FontStyle.Regular, GraphicsUnit.Pixel);
            const string Hint = "1-9 zoom   Enter click   R right-click   D double-click   Backspace back   Tab next monitor   Esc cancel";
            var hintSize = g.MeasureString(Hint, hintFont);
            var hx = (ClientSize.Width - hintSize.Width) / 2;
            var hy = ClientSize.Height - hintSize.Height - 16;
            g.FillRectangle(labelBack, hx - 8, hy - 3, hintSize.Width + 16, hintSize.Height + 6);
            g.DrawString(Hint, hintFont, Brushes.White, hx, hy);
        }
    }

    // ---- native --------------------------------------------------------------------------------

    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);
}
