using System.Runtime.InteropServices;

namespace TimeTracker.App.Interop;

/// <summary>
/// Tints a window's native title bar dark via DWM, for the one window in the app (the
/// resizable timesheet view) that intentionally uses standard OS chrome instead of a custom
/// borderless one — so it still matches the app's dark theme instead of showing a stock white
/// title bar.
/// </summary>
public static class DarkTitleBar
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public static void Apply(IntPtr hwnd, bool dark)
    {
        int value = dark ? 1 : 0;
        try { DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int)); }
        catch { /* older Windows without this attribute — leave the default title bar */ }
    }
}
