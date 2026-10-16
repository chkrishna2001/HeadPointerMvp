using System.Runtime.InteropServices;
using HeadPointerMvp.Infrastructure;

namespace HeadPointerMvp.Services;

/// <summary>
/// Always-on triggers that work while a full-screen remote-desktop window has focus. A normal global hotkey
/// (RegisterHotKey) can be pre-empted by the remote client; a low-level hook sees keys and mouse buttons
/// before any window does, and can swallow them so the remote session never receives them.
/// Every trigger that fires is logged as "[trigger] ...", so the log shows whether a given input
/// survives a given focused app. Must be created on the UI thread (the hooks need its message loop).
/// </summary>
public sealed class TriggerHookService(FileLogger logger, Action<Action> post) : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int WmXButtonDown = 0x020B;
    private const int WmXButtonUp = 0x020C;

    private readonly List<(uint Modifiers, uint Key, string Name, Action Action)> _chords = [];
    private readonly HashSet<int> _swallowedKeys = [];
    private int _mouseButton; // 0 = none, 1 = XButton1 (back), 2 = XButton2 (forward)
    private Action? _mouseAction;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private HookProc? _keyboardProc; // fields so the GC can't collect delegates the OS is still calling
    private HookProc? _mouseProc;

    public void AddChord(string shortcut, Action action)
    {
        try
        {
            var (modifiers, key) = HotKeyService.Parse(shortcut);
            _chords.Add((modifiers, key, shortcut, action));
        }
        catch (Exception ex)
        {
            _ = logger.InfoAsync($"[trigger] chord {shortcut} rejected: {ex.Message}");
        }
    }

    /// <param name="button">"XButton1" (back), "XButton2" (forward) or "None".</param>
    public void SetMouseButton(string button, Action action)
    {
        _mouseButton = button.Trim().ToLowerInvariant() switch { "xbutton1" => 1, "xbutton2" => 2, _ => 0 };
        _mouseAction = action;
    }

    public void Install()
    {
        var module = GetModuleHandle(null);
        if (_chords.Count > 0)
        {
            _keyboardProc = KeyboardCallback;
            _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, module, 0);
            if (_keyboardHook == IntPtr.Zero) _ = logger.InfoAsync($"[trigger] keyboard hook failed (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        if (_mouseButton != 0)
        {
            _mouseProc = MouseCallback;
            _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, module, 0);
            if (_mouseHook == IntPtr.Zero) _ = logger.InfoAsync($"[trigger] mouse hook failed (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        _ = logger.InfoAsync($"[trigger] installed: {_chords.Count} chord(s), mouse button {(_mouseButton == 0 ? "none" : "XButton" + _mouseButton)}.");
    }

    private IntPtr KeyboardCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var vk = Marshal.ReadInt32(lParam);
            var message = wParam.ToInt32();
            if (message is WmKeyDown or WmSysKeyDown)
            {
                var modifiers = CurrentModifiers();
                foreach (var chord in _chords)
                {
                    if (chord.Key != (uint)vk || chord.Modifiers != modifiers) continue;
                    _swallowedKeys.Add(vk);
                    _ = logger.InfoAsync($"[trigger] chord {chord.Name} seen.");
                    var action = chord.Action;
                    post(action);
                    return 1;
                }
            }
            else if (_swallowedKeys.Remove(vk))
            {
                return 1; // also swallow the matching key-up so the focused app never sees half a keystroke
            }
        }

        return CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private IntPtr MouseCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var message = wParam.ToInt32();
            if (message is WmXButtonDown or WmXButtonUp)
            {
                // MSLLHOOKSTRUCT: POINT (8 bytes), then mouseData; the high word says which X button.
                var which = (Marshal.ReadInt32(lParam, 8) >> 16) & 0xFFFF;
                if (which == _mouseButton)
                {
                    if (message == WmXButtonDown)
                    {
                        _ = logger.InfoAsync($"[trigger] mouse XButton{which} seen.");
                        if (_mouseAction is { } action) post(action);
                    }

                    return 1;
                }
            }
        }

        return CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private static uint CurrentModifiers()
    {
        uint modifiers = 0;
        if ((GetAsyncKeyState(0x12) & 0x8000) != 0) modifiers |= 0x0001; // Alt
        if ((GetAsyncKeyState(0x11) & 0x8000) != 0) modifiers |= 0x0002; // Ctrl
        if ((GetAsyncKeyState(0x10) & 0x8000) != 0) modifiers |= 0x0004; // Shift
        return modifiers;
    }

    public void Dispose()
    {
        if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        _keyboardHook = _mouseHook = IntPtr.Zero;
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
