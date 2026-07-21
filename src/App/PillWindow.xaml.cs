using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TimeTracker.App.UI;

namespace TimeTracker.App;

public partial class PillWindow : Window
{
    private const double ExpandedWidth = 216;
    private const double CollapsedWidth = 60;
    private const double EdgeMargin = 14;  // resting gap from the screen edge
    private const double SnapZone = 90;     // how near an edge triggers a corner/edge snap

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);

    private bool _dragging, _moved, _collapsed;
    private Point _cursorStart, _winStart;
    private Point _lastPos; private double _lastTime; private double _vx, _vy;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private static App A => App.Current;

    public PillWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        A.Tick += UpdateTime;
        A.StateChanged += UpdateState;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        var wa = WorkArea();
        var st = A.Tracker.State;
        double left = st.PillLeft ?? (wa.Right - Width - 24);
        double top = st.PillTop ?? (wa.Bottom - Height - 24);
        Left = Clamp(left, wa.Left, wa.Right - Width);
        Top = Clamp(top, wa.Top, wa.Bottom - Height);
        UpdateState();
        UpdateTime();
    }

    // ---------------- live content ----------------

    private void UpdateTime()
    {
        TimeText.Text = A.Tracker.IsRunning ? App.FormatElapsed(A.Tracker.CurrentElapsedSeconds) : "--:--:--";
    }

    private void UpdateState()
    {
        bool running = A.Tracker.IsRunning;
        var show = A.Tracker.Active ?? A.Tracker.LastActive;

        Dot.Fill = show is not null ? ColorUtil.Brush(show.Color) : (Brush)FindResource("TextFaint");
        Dot.Opacity = running ? 1.0 : 0.5;
        CodeText.Text = show?.Code ?? "Not tracking";
        ToggleBtn.Content = running ? "■" : "▶";
        ToggleBtn.ToolTip = running ? "Stop" : $"Start {show?.Code ?? "project"}";
        UpdateTime();
    }

    // ---------------- toggle + collapse + open ----------------

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (A.Tracker.IsRunning) A.StopWithPrompt();
        else A.ResumeLast();
    }

    private void Dot_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        SetCollapsed(!_collapsed);
    }

    private void SetCollapsed(bool collapsed)
    {
        _collapsed = collapsed;
        Body.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        Grip.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;

        double targetW = collapsed ? CollapsedWidth : ExpandedWidth;
        var wa = WorkArea();
        // Keep the same screen edge fixed while resizing, and stay on-screen.
        bool rightSide = (Left + Width / 2) > (wa.Left + wa.Width / 2);
        double targetLeft = rightSide ? (Left + Width - targetW) : Left;
        targetLeft = Clamp(targetLeft, wa.Left + EdgeMargin, wa.Right - targetW - EdgeMargin);

        Animate(WidthProperty, targetW, 240);
        Animate(LeftProperty, targetLeft, 240);
    }

    private void Pill_RightClick(object sender, MouseButtonEventArgs e) => A.ShowMainWindow();

    // ---------------- drag + momentum ----------------

    private void Pill_Down(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, Dot) || ToggleBtn.IsMouseOver) return;

        if (e.ClickCount == 2) { A.ShowMainWindow(); return; }  // double-click opens the app

        StopAnimations();
        _dragging = true; _moved = false;
        _cursorStart = CursorDip();
        _winStart = new Point(Left, Top);
        _lastPos = _cursorStart; _lastTime = _clock.Elapsed.TotalMilliseconds; _vx = _vy = 0;
        Pill.CaptureMouse();
    }

    private void Pill_Move(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var c = CursorDip();
        double now = _clock.Elapsed.TotalMilliseconds;
        double dt = Math.Max(1, now - _lastTime);
        _vx = (c.X - _lastPos.X) / dt;   // px per ms
        _vy = (c.Y - _lastPos.Y) / dt;
        _lastPos = c; _lastTime = now;

        var wa = WorkArea();
        double nl = Clamp(_winStart.X + (c.X - _cursorStart.X), wa.Left, wa.Right - Width);
        double nt = Clamp(_winStart.Y + (c.Y - _cursorStart.Y), wa.Top, wa.Bottom - Height);
        if (Math.Abs(nl - _winStart.X) + Math.Abs(nt - _winStart.Y) > 3) _moved = true;
        Left = nl; Top = nt;
    }

    private void Pill_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Pill.ReleaseMouseCapture();
        if (_moved) Settle();
    }

    /// <summary>
    /// One smooth eased motion from the release point to a tidy margin: projects a short
    /// glide from the fling velocity, then snaps to the nearest edge/corner if close — so it
    /// drifts to a stop instead of slamming the edge and bouncing back.
    /// </summary>
    private void Settle()
    {
        var wa = WorkArea();
        double projX = Left + _vx * 110;   // ~110 ms of projected glide
        double projY = Top + _vy * 110;

        double minL = wa.Left + EdgeMargin, maxL = wa.Right - Width - EdgeMargin;
        double minT = wa.Top + EdgeMargin, maxT = wa.Bottom - Height - EdgeMargin;

        double tx = projX < wa.Left + EdgeMargin + SnapZone ? minL
                  : projX > wa.Right - Width - EdgeMargin - SnapZone ? maxL
                  : Clamp(projX, minL, maxL);
        double ty = projY < wa.Top + EdgeMargin + SnapZone ? minT
                  : projY > wa.Bottom - Height - EdgeMargin - SnapZone ? maxT
                  : Clamp(projY, minT, maxT);

        double dist = Math.Abs(tx - Left) + Math.Abs(ty - Top);
        int ms = (int)Clamp(dist * 1.4, 280, 620);
        Animate(LeftProperty, tx, ms, persistPosition: true);
        Animate(TopProperty, ty, ms, persistPosition: true);
    }

    // ---------------- animation helpers ----------------

    private void Animate(DependencyProperty prop, double to, int ms, bool persistPosition = false)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 }
        };
        anim.Completed += (_, _) =>
        {
            BeginAnimation(prop, null);
            SetValue(prop, to);
            if (persistPosition) A.Tracker.SavePillPosition(Left, Top);
        };
        BeginAnimation(prop, anim);
    }

    private void StopAnimations()
    {
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
        BeginAnimation(WidthProperty, null);
    }

    // ---------------- geometry ----------------

    private Point CursorDip()
    {
        GetCursorPos(out var p);
        var src = PresentationSource.FromVisual(this);
        if (src?.CompositionTarget is { } ct)
            return ct.TransformFromDevice.Transform(new Point(p.X, p.Y));
        return new Point(p.X, p.Y);
    }

    /// <summary>Working area (DIP) of the monitor the pill currently sits on — multi-monitor safe.</summary>
    private Rect WorkArea()
    {
        var src = PresentationSource.FromVisual(this);
        var toDip = src?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;

        // Device-pixel centre of the pill.
        var toDev = src?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var centerDev = toDev.Transform(new Point(Left + Width / 2, Top + Height / 2));
        var screen = System.Windows.Forms.Screen.FromPoint(
            new System.Drawing.Point((int)centerDev.X, (int)centerDev.Y));
        var wa = screen.WorkingArea;

        var tl = toDip.Transform(new Point(wa.Left, wa.Top));
        var br = toDip.Transform(new Point(wa.Right, wa.Bottom));
        return new Rect(tl, br);
    }

    private static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
}
