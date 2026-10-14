using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace TimeTracker.App.UI;

/// <summary>
/// Blurs and dims one other app's window (e.g. the terminal that triggered a popup) behind a
/// popup, via a non-activating overlay covering that window's bounds, slotted into z-order
/// directly beneath the popup.
/// Preferred: a live, compositor-drawn acrylic blur (undocumented SetWindowCompositionAttribute —
/// the documented DWMWA_SYSTEMBACKDROP_TYPE acrylic switches off on inactive windows, and these
/// overlays never activate) in one window created once and reused (see <see cref="Prewarm"/>) so
/// showing it costs no window creation. Fallback, when the user has transparency effects off: a
/// blurred screenshot of the target window's area.
/// Either way the overlay fades in via its layered-window alpha (applied by DWM, so it's cheap and
/// keeps WPF on the hardware render path), and is placed under the popup BEFORE it's shown so it
/// never covers the popup for a frame.
/// <see cref="Capture"/> and <see cref="Show"/> are split so the screenshot fallback is taken
/// before the popup appears, while the overlay is shown once the popup has drawn.
/// </summary>
public sealed class BackdropBlur
{
    private const double Downscale = 0.25; // shrinking then stretching back does most of the blurring, cheaply
    private const double BlurRadius = 6;   // in downscaled pixels
    private const double DimOpacity = 0.28;
    private const uint AcrylicTint = 0x50101010; // AABBGGRR: dark, ~31% — the dim on top of the blur
    private const double FadeInMs = 180;

    private static Window? _acrylic;
    private static BackdropBlur? _acrylicOwner;
    private static Action? _acrylicClick;

    private readonly System.Drawing.Rectangle _bounds;
    private readonly BitmapSource? _shot; // null = acrylic
    private Window? _window;

    private BackdropBlur(System.Drawing.Rectangle bounds, BitmapSource? shot) { _bounds = bounds; _shot = shot; }

    /// <summary>Create the shared acrylic overlay ahead of time (hidden), so the first popup
    /// doesn't pay for it. Safe to call repeatedly.</summary>
    public static void Prewarm()
    {
        if (_acrylic is not null || !AcrylicAvailable()) return;
        try
        {
            _acrylic = AcrylicWindow();
            new WindowInteropHelper(_acrylic).EnsureHandle();
        }
        catch { _acrylic = null; }
    }

    /// <summary>The content area of <paramref name="target"/> in physical screen pixels — its
    /// client area, minus Windows Terminal's tab row — or null when there's nothing sensible to
    /// cover (no window, one of ours, minimised, desktop/taskbar).</summary>
    public static System.Drawing.Rectangle? ContentBounds(IntPtr target)
    {
        if (target == IntPtr.Zero || IsIconic(target) || !IsWindowVisible(target)) return null;
        GetWindowThreadProcessId(target, out uint pid);
        if (pid == (uint)Environment.ProcessId) return null;
        if (ClassOf(target) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return null;

        if (!GetClientRect(target, out RECT c)) return null;
        var origin = new POINT();
        ClientToScreen(target, ref origin);
        int top = origin.Y;
        // Windows Terminal draws its tabs inside the client area, under a drag-bar child window
        // as tall as the tab row — start below it.
        EnumChildWindows(target, (h, _) =>
        {
            if (IsWindowVisible(h) && ClassOf(h) == "DRAG_BAR_WINDOW_CLASS" && GetWindowRect(h, out RECT d))
                top = Math.Max(top, d.Bottom);
            return true;
        }, IntPtr.Zero);
        var bounds = System.Drawing.Rectangle.FromLTRB(origin.X, top, origin.X + c.Right, origin.Y + c.Bottom);
        return bounds.Width > 0 && bounds.Height > 0 ? bounds : null;
    }

    private static string ClassOf(IntPtr hwnd)
    {
        var cls = new System.Text.StringBuilder(64);
        GetClassName(hwnd, cls, cls.Capacity);
        return cls.ToString();
    }

    /// <summary>Prepare a blur over <paramref name="bounds"/> (physical screen pixels).</summary>
    public static BackdropBlur? Capture(System.Drawing.Rectangle bounds)
    {
        if (AcrylicAvailable()) return new BackdropBlur(bounds, null);
        try { return new BackdropBlur(bounds, Snapshot(bounds)); }
        catch { return null; }
    }

    /// <summary>Acrylic needs Windows 10 1803+ and "Transparency effects" on — with it off, DWM
    /// draws the acrylic as a flat solid colour.</summary>
    private static bool AcrylicAvailable()
    {
        if (Environment.OSVersion.Version.Build < 17134) return false;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("EnableTransparency") is not int v || v != 0;
        }
        catch { return true; }
    }

    /// <summary>Fade the overlay in directly beneath the already-visible <paramref name="popup"/>
    /// — callers show their popup first (calling this from its ContentRendered).
    /// <paramref name="onClick"/> runs when the backdrop is clicked.</summary>
    public BackdropBlur Show(Window popup, Action? onClick = null)
    {
        if (!popup.IsVisible) return this; // already answered before it finished drawing
        var above = new WindowInteropHelper(popup).Handle;
        if (_shot is null)
        {
            Prewarm();
            if (_acrylic is null) return this;
            _acrylicOwner = this;
            _acrylicClick = onClick;
            Present(_acrylic, _bounds, above);
            return this;
        }
        var w = _window = SnapshotWindow(_shot);
        if (onClick is not null) w.MouseLeftButtonDown += (_, _) => onClick();
        new WindowInteropHelper(w).EnsureHandle();
        Present(w, _bounds, above);
        return this;
    }

    public void Close()
    {
        _window?.Close();
        _window = null;
        if (_acrylicOwner == this && _acrylic is not null)
        {
            _acrylicOwner = null;
            _acrylicClick = null;
            SetAlpha(_acrylic, 0);
            _acrylic.Hide();
        }
    }

    /// <summary>Alpha 0, made topmost, sized and slotted under the popup while still hidden, then
    /// shown without activating and faded in. Re-enabled explicitly: a modal ShowDialog disables
    /// every window already on the thread, the reused overlay included, which would leave it deaf
    /// to clicks.</summary>
    private static void Present(Window w, System.Drawing.Rectangle bounds, IntPtr above)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        w.Tag = (bounds, above);
        SetAlpha(w, 0);
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        Place(w, bounds, above);
        w.Show();
        Place(w, bounds, above);
        EnableWindow(hwnd, true);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(10),
        };
        timer.Tick += (_, _) =>
        {
            if (!w.IsVisible) { timer.Stop(); return; }
            double t = Math.Min(1, clock.Elapsed.TotalMilliseconds / FadeInMs);
            SetAlpha(w, (byte)(255 * (1 - (1 - t) * (1 - t)))); // ease-out
            if (t >= 1) timer.Stop();
        };
        timer.Start();
    }

    /// <summary>Physical pixels (not DIPs) so mixed-DPI monitors line up; z-order directly under the popup.</summary>
    private static void Place(Window w, System.Drawing.Rectangle bounds, IntPtr above)
        => SetWindowPos(new WindowInteropHelper(w).Handle, above,
            bounds.Left, bounds.Top, bounds.Width, bounds.Height, SWP_NOACTIVATE);

    private static void SetAlpha(Window w, byte alpha)
        => SetLayeredWindowAttributes(new WindowInteropHelper(w).Handle, 0, alpha, LWA_ALPHA);

    /// <summary>Screen straight into a quarter-size bitmap in one GDI StretchBlt — far cheaper
    /// than a full-resolution CopyFromScreen followed by a resize.</summary>
    private static BitmapSource Snapshot(System.Drawing.Rectangle b)
    {
        int sw = Math.Max(1, (int)(b.Width * Downscale)), sh = Math.Max(1, (int)(b.Height * Downscale));
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr bmp = CreateCompatibleBitmap(screenDc, sw, sh);
        IntPtr old = SelectObject(memDc, bmp);
        try
        {
            SetStretchBltMode(memDc, HALFTONE);
            SetBrushOrgEx(memDc, 0, 0, IntPtr.Zero);
            StretchBlt(memDc, 0, 0, sw, sh, screenDc, b.Left, b.Top, b.Width, b.Height, SRCCOPY);
            SelectObject(memDc, old);
            // GDI leaves the alpha byte 0, which WPF reads as fully transparent (a black screen),
            // so the pixels are re-wrapped as Bgr32, which ignores alpha.
            var raw = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            int stride = sw * 4;
            var pixels = new byte[stride * sh];
            raw.CopyPixels(pixels, stride, 0);
            var src = BitmapSource.Create(sw, sh, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
            src.Freeze();
            return src;
        }
        finally
        {
            DeleteObject(bmp);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static Window AcrylicWindow()
    {
        // Not AllowsTransparency (WPF renders full-screen AllowsTransparency windows in software,
        // which made the first version take ~2s): a transparent WPF clear colour plus the DWM frame
        // extended over the whole client area lets the acrylic DWM draws behind it show through.
        var w = BaseWindow(new Grid { Background = Brushes.Transparent }, Brushes.Transparent);
        w.MouseLeftButtonDown += (_, _) => _acrylicClick?.Invoke();
        w.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            HwndSource.FromHwnd(hwnd).CompositionTarget.BackgroundColor = Colors.Transparent;
            var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref margins);
            EnableAcrylic(hwnd);
        };
        return w;
    }

    private static void EnableAcrylic(IntPtr hwnd)
    {
        var accent = new ACCENT_POLICY { AccentState = ACCENT_ENABLE_ACRYLICBLURBEHIND, GradientColor = AcrylicTint };
        int size = Marshal.SizeOf(accent);
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new WINDOWCOMPOSITIONATTRIBDATA { Attribute = WCA_ACCENT_POLICY, Data = ptr, SizeOfData = size };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    private static Window SnapshotWindow(BitmapSource image)
    {
        var img = new Image { Source = image, Stretch = Stretch.Fill, Effect = new BlurEffect { Radius = BlurRadius * 4 } };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.Linear);
        // Bleed the image past the edges so the blur's fade-to-transparent border is clipped off.
        img.Margin = new Thickness(-BlurRadius * 6);
        var root = new Grid { ClipToBounds = true, Background = Brushes.Black };
        root.Children.Add(img);
        root.Children.Add(new Rectangle { Fill = Brushes.Black, Opacity = DimOpacity });
        return BaseWindow(root, Brushes.Black);
    }

    /// <summary>Deliberately not WPF-Topmost: WPF applies that at Show by raising the window to
    /// the top of the topmost band — over the popup for a frame (the flicker). Present makes it
    /// topmost itself while it's still hidden. Layered (alpha only) for the fade.</summary>
    private static Window BaseWindow(UIElement content, Brush background)
    {
        var w = new Window
        {
            WindowStyle = WindowStyle.None, Background = background,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false,
            Focusable = false, Content = content,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000, Top = -32000, Width = 1, Height = 1,
        };
        w.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            SetWindowLong(hwnd, GWL_EXSTYLE,
                GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_LAYERED);
            SetLayeredWindowAttributes(hwnd, 0, 0, LWA_ALPHA);
        };
        // WPF resizes to its own suggested rect on a DPI change — put it back.
        w.DpiChanged += (_, _) => w.Dispatcher.BeginInvoke(() =>
        {
            if (w.Tag is ValueTuple<System.Drawing.Rectangle, IntPtr> t) Place(w, t.Item1, t.Item2);
        });
        return w;
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint LWA_ALPHA = 0x2;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct ACCENT_POLICY { public int AccentState; public int AccentFlags; public uint GradientColor; public int AnimationId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWCOMPOSITIONATTRIBDATA { public int Attribute; public IntPtr Data; public int SizeOfData; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS { public int Left, Right, Top, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref POINT pt);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WINDOWCOMPOSITIONATTRIBDATA data);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr hwnd, bool enable);

    private const int HALFTONE = 4;
    private const int SRCCOPY = 0x00CC0020;

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] private static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr lppt);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(IntPtr dest, int xd, int yd, int wd, int hd,
        IntPtr src, int xs, int ys, int ws, int hs, int rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
