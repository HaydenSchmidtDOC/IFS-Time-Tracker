using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TimeTracker.App.UI;

namespace TimeTracker.App;

public partial class PillWindow : Window
{
    private const double ExpandedWidth = 248;
    private const double CollapsedWidth = 76;
    private const double EdgeMargin = 14;   // resting gap from the screen edge
    private const double SnapZone = 90;     // how near an edge triggers a corner/edge snap
    private const double FrictionPerFrame = 0.90; // velocity multiplier per ~16ms frame

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);

    private bool _dragging, _moved, _collapsed;
    private Point _cursorStart, _winStart;
    private Point _lastPos; private double _lastTimeMs; private double _vx, _vy; // px/ms
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    // ---- single manual per-frame animator (WPF's BeginAnimation on Window.Left/Top does not
    // reliably move the underlying OS window, so position is driven imperatively every frame) ----
    private enum AnimMode { None, Glide, Tween }
    private AnimMode _anim = AnimMode.None;
    private double _frameTimeMs;
    private double _tFromL, _tFromT, _tFromW, _tToL, _tToT, _tToW, _tElapsedMs, _tDurationMs;
    private Action? _tweenDone;

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
        CodeText.Text = show?.Code ?? "Idle";
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

        double targetW = collapsed ? CollapsedWidth : ExpandedWidth;
        var wa = WorkArea();
        // Keep the same screen edge fixed while resizing, and stay fully on-screen either way.
        bool rightSide = (Left + Width / 2) > (wa.Left + wa.Width / 2);
        double targetLeft = rightSide ? (Left + Width - targetW) : Left;
        targetLeft = Clamp(targetLeft, wa.Left + EdgeMargin, wa.Right - targetW - EdgeMargin);
        double targetTop = Clamp(Top, wa.Top + EdgeMargin, wa.Bottom - Height - EdgeMargin);

        StartTween(targetLeft, targetTop, targetW, 240, () => A.Tracker.SavePillPosition(Left, Top));
    }

    private void Pill_RightClick(object sender, MouseButtonEventArgs e) => A.ShowMainWindow();

    // ---------------- drag ----------------

    private void Pill_Down(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, Dot) || ToggleBtn.IsMouseOver) return;

        if (e.ClickCount == 2) { A.ShowMainWindow(); return; } // double-click opens the app

        StopAnim();
        _dragging = true; _moved = false;
        _cursorStart = CursorDip();
        _winStart = new Point(Left, Top);
        _lastPos = _cursorStart; _lastTimeMs = _clock.Elapsed.TotalMilliseconds; _vx = _vy = 0;
        Pill.CaptureMouse();
    }

    private void Pill_Move(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var c = CursorDip();
        double now = _clock.Elapsed.TotalMilliseconds;
        double dt = Math.Max(1, now - _lastTimeMs);
        _vx = (c.X - _lastPos.X) / dt;   // px per ms
        _vy = (c.Y - _lastPos.Y) / dt;
        _lastPos = c; _lastTimeMs = now;

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
        if (_moved) StartGlide();
        else A.Tracker.SavePillPosition(Left, Top);
    }

    // ---------------- manual per-frame animation ----------------
    // WPF's BeginAnimation on Window.Left/Top is known to not reliably reposition the actual
    // OS window frame-by-frame, so both the momentum glide and the collapse/expand resize are
    // driven by setting Left/Top/Width directly on every CompositionTarget.Rendering tick —
    // the same mechanism the drag itself already uses successfully.

    private void StartGlide()
    {
        _anim = AnimMode.Glide;
        _frameTimeMs = _clock.Elapsed.TotalMilliseconds;
        HookFrame();
    }

    private void StartTween(double toLeft, double toTop, double toWidth, double ms, Action? onDone = null)
    {
        _tFromL = Left; _tFromT = Top; _tFromW = Width;
        _tToL = toLeft; _tToT = toTop; _tToW = toWidth;
        _tElapsedMs = 0; _tDurationMs = Math.Max(1, ms);
        _tweenDone = onDone;
        _anim = AnimMode.Tween;
        _frameTimeMs = _clock.Elapsed.TotalMilliseconds;
        HookFrame();
    }

    private void StopAnim()
    {
        if (_anim == AnimMode.None) return;
        _anim = AnimMode.None;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void HookFrame()
    {
        CompositionTarget.Rendering -= OnFrame; // avoid double-subscribe if switching modes
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        double dt = Math.Min(40, now - _frameTimeMs); // clamp to avoid a big jump after a hitch
        _frameTimeMs = now;

        switch (_anim)
        {
            case AnimMode.Glide: StepGlide(dt); break;
            case AnimMode.Tween: StepTween(dt); break;
        }
    }

    private void StepGlide(double dt)
    {
        var wa = WorkArea();
        double nx = Left + _vx * dt;
        double ny = Top + _vy * dt;
        double cx = Clamp(nx, wa.Left, wa.Right - Width);
        double cy = Clamp(ny, wa.Top, wa.Bottom - Height);
        if (cx != nx) _vx = 0;   // hit a wall — stop that axis rather than bouncing
        if (cy != ny) _vy = 0;
        Left = cx; Top = cy;

        double decay = Math.Pow(FrictionPerFrame, dt / 16.0);
        _vx *= decay; _vy *= decay;

        if (Math.Abs(_vx) < 0.02 && Math.Abs(_vy) < 0.02)
        {
            var (tx, ty) = SnapTarget(wa);
            double dist = Math.Abs(tx - Left) + Math.Abs(ty - Top);
            StartTween(tx, ty, Width, Clamp(dist * 1.5, 260, 560), () => A.Tracker.SavePillPosition(Left, Top));
        }
    }

    private void StepTween(double dt)
    {
        _tElapsedMs += dt;
        double t = Math.Min(1, _tElapsedMs / _tDurationMs);
        double ease = 1 - Math.Pow(1 - t, 3); // ease-out cubic — decelerates into the rest position
        Left = _tFromL + (_tToL - _tFromL) * ease;
        Top = _tFromT + (_tToT - _tFromT) * ease;
        Width = _tFromW + (_tToW - _tFromW) * ease;

        if (t >= 1)
        {
            StopAnim();
            var done = _tweenDone; _tweenDone = null;
            done?.Invoke();
        }
    }

    /// <summary>Nearest resting edge/corner for the pill's current size, always fully on-screen.</summary>
    private (double x, double y) SnapTarget(Rect wa)
    {
        double minL = wa.Left + EdgeMargin, maxL = wa.Right - Width - EdgeMargin;
        double minT = wa.Top + EdgeMargin, maxT = wa.Bottom - Height - EdgeMargin;

        double tx = Left < wa.Left + EdgeMargin + SnapZone ? minL
                  : Left > wa.Right - Width - EdgeMargin - SnapZone ? maxL
                  : Clamp(Left, minL, maxL);
        double ty = Top < wa.Top + EdgeMargin + SnapZone ? minT
                  : Top > wa.Bottom - Height - EdgeMargin - SnapZone ? maxT
                  : Clamp(Top, minT, maxT);
        return (tx, ty);
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
