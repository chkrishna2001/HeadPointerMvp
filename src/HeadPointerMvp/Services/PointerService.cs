using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using HeadPointerMvp.Infrastructure;

namespace HeadPointerMvp.Services;

public sealed partial class PointerService(MonitorService monitors, FileLogger logger)
{
    public void MoveToMonitor(int monitorIndex, bool activateWindow = true, bool allowSimulatedClickActivation = true)
    {
        var screen = monitors.GetTarget(monitorIndex);
        var area = screen.WorkingArea;
        var moved = SetCursorPos(area.Left + (area.Width / 2), area.Top + (area.Height / 2));
        if (!moved) _ = logger.InfoAsync($"[pointer] SetCursorPos failed (Win32 error {Marshal.GetLastWin32Error()}).");
        if (activateWindow) ActivateTopWindow(screen, allowSimulatedClickActivation);
    }

    public enum ClickKind
    {
        Left,
        Right,
        Double
    }

    /// <summary>Moves the cursor to a screen point and performs a real click there (used by the zoom grid).</summary>
    public void ClickAt(int x, int y, ClickKind kind)
    {
        SetCursorPos(x, y);
        var (down, up) = kind == ClickKind.Right ? (MouseEventFRightDown, MouseEventFRightUp) : (MouseEventFLeftDown, MouseEventFLeftUp);
        var count = kind == ClickKind.Double ? 2 : 1;
        var events = new List<Input>();
        for (var i = 0; i < count; i++)
        {
            events.Add(MouseInput(down));
            events.Add(MouseInput(up));
        }

        var sent = SendInput((uint)events.Count, events.ToArray(), InputSize);
        if (sent != events.Count) _ = logger.InfoAsync($"[grid] click blocked: {sent}/{events.Count} events sent (Win32 error {Marshal.GetLastWin32Error()}).");
    }

    public void MoveTo(int x, int y) => SetCursorPos(x, y);

    /// <summary>
    /// Brings the topmost visible window overlapping the target monitor to the foreground, so typing
    /// after a head-tracking move lands in that window.
    /// </summary>
    private void ActivateTopWindow(Screen screen, bool allowSimulatedClickActivation)
    {
        var target = FindTopWindowOnMonitor(screen);
        if (target == IntPtr.Zero)
        {
            _ = logger.InfoAsync("[activate] no eligible window found on the target monitor.");
            return;
        }

        if (target == GetForegroundWindow())
        {
            _ = logger.InfoAsync("[activate] target window is already foreground; nothing to do.");
            return;
        }

        if (TryActivateViaAttachThreadInput(target))
        {
            _ = logger.InfoAsync("[activate] SetForegroundWindow succeeded.");
            return;
        }

        if (!allowSimulatedClickActivation)
        {
            _ = logger.InfoAsync("[activate] SetForegroundWindow was refused and the simulated-click fallback is turned off.");
            return;
        }

        // SetForegroundWindow can be silently refused -- most commonly when the target window
        // belongs to a more-privileged (elevated) process than this one. A real click is usually
        // honored by the window manager where SetForegroundWindow was refused, so simulate one at the
        // cursor's new position (MoveToMonitor already placed it there) as a fallback. Right-click
        // rather than left, so it can't accidentally trigger a button/link under the cursor, followed
        // by a brief pause then Escape in case that opened a context menu, so nothing is left visibly
        // open. Note Windows' UIPI can still block synthetic input into a genuinely higher-privilege
        // (elevated) window even via SendInput -- if that's the case here, no non-elevated process can
        // force activation of it, full stop; the logging below at least tells us which case this is.
        _ = logger.InfoAsync("[activate] SetForegroundWindow was refused; trying a simulated click.");
        SimulateActivationClick();
    }

    /// <summary>Returns true once the target window is confirmed foreground, false if the OS refused the request.</summary>
    private static bool TryActivateViaAttachThreadInput(IntPtr target)
    {
        var foreground = GetForegroundWindow();
        var foregroundThread = GetWindowThreadProcessId(foreground, IntPtr.Zero);
        var targetThread = GetWindowThreadProcessId(target, IntPtr.Zero);
        var currentThread = GetCurrentThreadId();

        var attachedToForeground = foregroundThread != 0 && foregroundThread != currentThread && AttachThreadInput(currentThread, foregroundThread, true);
        var attachedToTarget = targetThread != 0 && targetThread != currentThread && targetThread != foregroundThread && AttachThreadInput(currentThread, targetThread, true);

        bool succeeded;
        try
        {
            SetForegroundWindow(target);
            BringWindowToTop(target);

            // SetForegroundWindow only changes which window is visually on top/highlighted (global
            // z-order state) -- it does NOT transfer actual keyboard input focus, which is a separate
            // per-thread concept. Without this explicit SetFocus, a window could look active while
            // keystrokes kept going to whatever previously had focus. SetFocus only takes effect
            // reliably when called by a thread attached to the target's input queue (which is exactly
            // what AttachThreadInput set up above), so it has to happen here, before detaching below.
            SetFocus(target);

            // GetForegroundWindow() reflects global state immediately either way; checking it while
            // still attached (rather than after detaching) is what makes this check meaningful instead
            // of just re-observing our own SetForegroundWindow call's surface-level effect.
            succeeded = GetForegroundWindow() == target;
        }
        finally
        {
            if (attachedToTarget) AttachThreadInput(currentThread, targetThread, false);
            if (attachedToForeground) AttachThreadInput(currentThread, foregroundThread, false);
        }

        return succeeded;
    }

    private void SimulateActivationClick()
    {
        var click = new[]
        {
            MouseInput(MouseEventFRightDown),
            MouseInput(MouseEventFRightUp)
        };
        var clickSent = SendInput((uint)click.Length, click, InputSize);
        if (clickSent != click.Length)
        {
            // SendInput returns how many events it actually injected; anything less than requested
            // means the rest were blocked -- most likely Windows' UIPI refusing synthetic input into
            // a higher-privilege (elevated) window, which no non-elevated process can override. This
            // was never checked before, so a blocked click previously failed completely silently.
            var error = Marshal.GetLastWin32Error();
            _ = logger.InfoAsync($"[activate] simulated click was blocked: only {clickSent}/{click.Length} events sent (Win32 error {error}).");
            return;
        }

        // Give the target a moment to actually process the click (and, if applicable, start opening
        // a context menu) before we dismiss it -- sending Escape in the same instant risks the two
        // events coalescing or racing, silently discarding both.
        Thread.Sleep(60);

        var dismissMenu = new[]
        {
            KeyInput(VkEscape, keyUp: false),
            KeyInput(VkEscape, keyUp: true)
        };
        var escapeSent = SendInput((uint)dismissMenu.Length, dismissMenu, InputSize);
        if (escapeSent != dismissMenu.Length)
        {
            var error = Marshal.GetLastWin32Error();
            _ = logger.InfoAsync($"[activate] dismiss-Escape was blocked: only {escapeSent}/{dismissMenu.Length} events sent (Win32 error {error}).");
        }
    }

    private static Input MouseInput(uint flags) => new()
    {
        Type = InputTypeMouse,
        Union = new InputUnion { Mouse = new MouseInputData { Flags = flags } }
    };

    private static Input KeyInput(ushort virtualKey, bool keyUp) => new()
    {
        Type = InputTypeKeyboard,
        Union = new InputUnion { Keyboard = new KeyboardInputData { VirtualKey = virtualKey, Flags = keyUp ? KeyEventFKeyUp : 0 } }
    };

    // The taskbar and desktop host windows are always-on-top/always-present, so they'd otherwise win
    // the "first match in Z-order" search on whichever monitor holds them.
    private static readonly HashSet<string> ExcludedClassNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW"
    };

    /// <summary>
    /// Finds the topmost real window whose primary monitor (per Windows' own MonitorFromWindow, which
    /// picks whichever monitor contains the largest portion of the window) is the target screen.
    /// Deliberately not a simple bounds-intersection check: that accepted ANY overlap at all, so a
    /// window merely touching the target monitor's edge by a pixel -- most commonly whatever window
    /// currently has real focus, since it's usually topmost in Z-order regardless of which monitor
    /// it's actually on -- got wrongly treated as "the window on that monitor, already active,
    /// nothing to do," silently skipping the window actually sitting there.
    /// </summary>
    private static IntPtr FindTopWindowOnMonitor(Screen screen)
    {
        var screenRect = new Rect { Left = screen.Bounds.Left, Top = screen.Bounds.Top, Right = screen.Bounds.Right, Bottom = screen.Bounds.Bottom };
        var targetMonitor = MonitorFromRect(ref screenRect, MonitorDefaultToNearest);

        var found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            if (GetWindowTextLength(hwnd) == 0) return true;
            if ((GetWindowLongW(hwnd, GwlExStyle) & WsExToolWindow) != 0) return true;
            if (ExcludedClassNames.Contains(GetClassName(hwnd))) return true;
            if (MonitorFromWindow(hwnd, MonitorDefaultToNearest) != targetMonitor) return true;

            found = hwnd;
            return false; // EnumWindows visits in top-to-bottom Z-order, so the first match wins.
        }, IntPtr.Zero);

        return found;
    }

    private static string GetClassName(IntPtr hwnd)
    {
        var buffer = new System.Text.StringBuilder(256);
        GetClassNameNative(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetCursorPos(int x, int y);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BringWindowToTop(IntPtr hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr SetFocus(IntPtr hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(IntPtr hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static partial int GetWindowLongW(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromWindow(IntPtr hWnd, uint dwFlags);

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromRect(ref Rect rect, uint dwFlags);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    private static partial int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetClassNameNative(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint numberOfInputs, Input[] inputs, int sizeOfInputStructure);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private static readonly int InputSize = Marshal.SizeOf<Input>();

    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const uint InputTypeMouse = 0;
    private const uint InputTypeKeyboard = 1;
    private const uint MouseEventFLeftDown = 0x0002;
    private const uint MouseEventFLeftUp = 0x0004;
    private const uint MouseEventFRightDown = 0x0008;
    private const uint MouseEventFRightUp = 0x0010;
    private const uint KeyEventFKeyUp = 0x0002;
    private const ushort VkEscape = 0x1B;
    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInputData Mouse;
        [FieldOffset(0)] public KeyboardInputData Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInputData
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInputData
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }
}
