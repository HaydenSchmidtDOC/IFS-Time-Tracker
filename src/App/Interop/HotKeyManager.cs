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

    private sealed class Registration
    {
        public int Id;
        public uint Modifiers;
        public uint VirtualKey;
        public Action Callback = null!;
        public bool Active;
    }

    private readonly HwndSource _source;
    private readonly List<Registration> _registrations = new();
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
        uint mod = (uint)modifiers | MOD_NOREPEAT;
        if (!RegisterHotKey(_source.Handle, id, mod, vk)) return false;
        _registrations.Add(new Registration { Id = id, Modifiers = mod, VirtualKey = vk, Callback = callback, Active = true });
        return true;
    }

    /// <summary>
    /// Release all hotkeys without forgetting them. RegisterHotKey is OS-level and steals the
    /// key combination system-wide, including from whatever window currently has focus — so
    /// while the quick switcher is open, holding Ctrl and pressing Up/Down again to navigate
    /// was being intercepted as "open the switcher" (a no-op re-activate) instead of reaching
    /// the switcher's own key handling. Pausing while it's open, and resuming on close, lets
    /// Ctrl+Up/Down behave as normal keyboard input to whichever window has focus in between.
    /// </summary>
    public void PauseAll()
    {
        foreach (var r in _registrations.Where(r => r.Active))
        {
            UnregisterHotKey(_source.Handle, r.Id);
            r.Active = false;
        }
    }

    public void ResumeAll()
    {
        foreach (var r in _registrations.Where(r => !r.Active))
            if (RegisterHotKey(_source.Handle, r.Id, r.Modifiers, r.VirtualKey))
                r.Active = true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            var reg = _registrations.FirstOrDefault(r => r.Id == wParam.ToInt32());
            if (reg is not null) { reg.Callback(); handled = true; }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var r in _registrations.Where(r => r.Active)) UnregisterHotKey(_source.Handle, r.Id);
        _registrations.Clear();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
