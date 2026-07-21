using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace TimeTracker.App.Interop;

/// <summary>
/// Registers system-wide hotkeys via a message-only window, so shortcuts like
/// Ctrl+Up/Down fire even when the app is minimised or unfocused.
/// </summary>
public sealed class HotKeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _handlers = new();
    private int _nextId = 1;

    public HotKeyManager()
    {
        // HWND_MESSAGE (-3) => a message-only window: no UI, just receives WM_HOTKEY.
        var p = new HwndSourceParameters("TimeTracker.HotKeys")
        {
            ParentWindow = new IntPtr(-3),
            Width = 0,
            Height = 0,
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    /// <summary>Register a global hotkey. WPF ModifierKeys map 1:1 to Win32 MOD_* flags.</summary>
    public bool Register(ModifierKeys modifiers, Key key, Action callback)
    {
        int id = _nextId++;
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (!RegisterHotKey(_source.Handle, id, (uint)modifiers | MOD_NOREPEAT, vk))
            return false;
        _handlers[id] = callback;
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _handlers.TryGetValue(wParam.ToInt32(), out var cb))
        {
            cb();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _handlers.Keys) UnregisterHotKey(_source.Handle, id);
        _handlers.Clear();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
