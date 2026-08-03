using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TimeTracker.App.Interop;

/// <summary>
/// Stops Windows clamping a borderless (AllowsTransparency + WindowStyle="None") window wider
/// than its declared Width. On Windows 11 24H2, WM_GETMINMAXINFO's default ptMinTrackSize for a
/// layered window comes back around 136 DIP wide regardless of ResizeMode="NoResize" — NoResize
/// only removes the resize *border/handles*, it doesn't stop Windows applying its own minimum
/// track size to the underlying HWND. Since SizeToContent="Height" then measures/arranges within
/// whatever width Windows actually granted the HWND rather than the XAML-declared Width, the
/// whole window (and everything inside the Grid/StackPanel that stretches to fill it) renders
/// clamped down to that ~136-160px minimum instead of its real width. Handling WM_GETMINMAXINFO
/// ourselves and overwriting ptMinTrackSize with the window's own actual size forces Windows to
/// grant the full width back.
/// </summary>
public static class MinTrackSizeFix
{
    private const int WM_GETMINMAXINFO = 0x0024;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    /// <summary>Call once the window's HWND exists (its SourceInitialized handler). Reads the
    /// window's own Width/MinHeight in device pixels and pins ptMinTrackSize to that so Windows
    /// stops enforcing its own larger minimum.</summary>
    public static void Apply(Window window)
    {
        var hwndSource = (HwndSource)PresentationSource.FromVisual(window)!;
        double dpiScale = hwndSource.CompositionTarget.TransformToDevice.M11;
        int minWidthPx = (int)Math.Round(window.Width * dpiScale);

        hwndSource.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg == WM_GETMINMAXINFO)
            {
                var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                mmi.ptMinTrackSize.X = minWidthPx;
                Marshal.StructureToPtr(mmi, lParam, true);
            }
            return IntPtr.Zero;
        });
    }
}
