using System.Runtime.InteropServices;

namespace TimeTracker.App.Interop;

/// <summary>
/// Restores the WS_SYSMENU/WS_MINIMIZEBOX style bits on a borderless (WindowStyle="None")
/// window. The taskbar's "click a window's own already-active button to minimize it, click
/// again to restore" toggle checks these bits on the underlying HWND before it'll act — not
/// anything about the window's actual visible chrome — and WindowStyle="None" strips both,
/// along with the caption itself. Without this, clicking the active app's taskbar icon just
/// re-activates the window every time instead of minimizing it. WPF's own WM_NCCALCSIZE
/// handling for WindowStyle="None" still suppresses any real native caption/box chrome from
/// being drawn, so setting these bits back doesn't bring back a visible system menu or min/
/// close boxes on the OS frame — only the custom title bar's own buttons remain visible.
/// </summary>
public static class TaskbarMinimizeFix
{
    private const int GWL_STYLE = -16;
    private const int WS_SYSMENU = 0x00080000;
    private const int WS_MINIMIZEBOX = 0x00020000;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    public static void Apply(IntPtr hwnd)
    {
        int style = GetWindowLong(hwnd, GWL_STYLE);
        SetWindowLong(hwnd, GWL_STYLE, style | WS_SYSMENU | WS_MINIMIZEBOX);
    }
}
