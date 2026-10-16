using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace HeadPointerMvp.Services;

public sealed partial class HotKeyService : NativeWindow, IDisposable
{
    private const int WmHotKey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private readonly Dictionary<int, Action> _handlers = [];
    private int _nextId = 1;

    public HotKeyService()
    {
        CreateHandle(new CreateParams());
    }

    public void Register(string shortcut, Action action)
    {
        var (modifiers, key) = Parse(shortcut);
        var id = _nextId++;
        if (!RegisterHotKey(Handle, id, modifiers, key))
            throw new InvalidOperationException($"Could not register hotkey {shortcut}. It may already be used.");
        _handlers[id] = action;
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmHotKey && _handlers.TryGetValue(message.WParam.ToInt32(), out var handler)) handler();
        base.WndProc(ref message);
    }

    public void Dispose()
    {
        foreach (var id in _handlers.Keys) UnregisterHotKey(Handle, id);
        _handlers.Clear();
        DestroyHandle();
    }

    internal static (uint modifiers, uint key) Parse(string shortcut)
    {
        var parts = shortcut.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !Enum.TryParse<Keys>(parts[^1], true, out var key))
            throw new ArgumentException($"Invalid hotkey '{shortcut}'. Example: Ctrl+Alt+H.", nameof(shortcut));
        var modifiers = parts[..^1].Aggregate(0u, (current, part) => current | part.ToLowerInvariant() switch
        {
            "ctrl" or "control" => ModControl,
            "alt" => ModAlt,
            "shift" => ModShift,
            _ => throw new ArgumentException($"Unsupported modifier '{part}'.", nameof(shortcut))
        });
        return (modifiers, (uint)key);
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(nint windowHandle, int id, uint modifiers, uint virtualKey);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(nint windowHandle, int id);
}
