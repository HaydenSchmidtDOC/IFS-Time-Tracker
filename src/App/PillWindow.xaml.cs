using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using TimeTracker.App.UI;

namespace TimeTracker.App;

public partial class PillWindow : Window
{
    // The Border inside the window has a 14px margin on every side (shadow bleed room — see
    // PillWindow.xaml), so all window-width constants below are the visible-pill size plus 28.
    private const double ShadowPad = 28;
    private const double CollapsedWidth = 76 + ShadowPad;
    private const double MinExpandedWidth = 180 + ShadowPad;
    private const double MaxExpandedWidth = 420 + ShadowPad;
    private const double EdgeMargin = 4;    // resting gap from the screen edge, outside the shadow padding
    private const double SnapZone = 90;     // how near an edge triggers a corner/edge snap
    private const double FrictionPerFrame = 0.94; // velocity multiplier per ~16ms frame — gentle, so a release visibly drifts
    private const double VelocitySmoothing = 0.35; // EMA factor for drag velocity samples

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);

    // ShowInTaskbar="False" only hides the taskbar button — Alt-Tab has its own rule (it skips
    // owned windows and anything carrying WS_EX_TOOLWINDOW). Without this the pill still shows
    // up as a tabbable window despite never appearing in the taskbar.
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_APPWINDOW = 0x00040000;
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    private bool _dragging, _moved, _collapsed, _loaded;
    private Point _cursorStart, _winStart;
    private Point _lastPos; private double _lastTimeMs; private double _vx, _vy; // px/ms, EMA-smoothed
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _expandedWidth = 248 + ShadowPad; // recomputed from the live project's text; see ComputeExpandedWidth

    // ---- single manual per-frame animator (WPF's BeginAnimation on Window.Left/Top does not
    // reliably move the underlying OS window, so position is driven imperatively every frame) ----
    private enum AnimMode { None, Glide, Tween }
    private AnimMode _anim = AnimMode.None;
    private double _frameTimeMs;
    private double _smoothedFrameMs = 16.0; // EMA of the real per-callback interval; see OnFrame
    private double _tFromL, _tFromT, _tFromW, _tToL, _tToT, _tToW, _tElapsedMs, _tDurationMs;
    private Action? _tweenDone;

    private static App A => App.Current;

    public PillWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        A.Tick += UpdateTime;
        A.StateChanged += UpdateState;
        // The idle-dot fallback colour (RefreshContent, below) is a FindResource lookup only
        // re-run on a state change — without this it'd stay stale after a live theme flip until
        // the next actual start/stop/switch.
        A.ThemeChanged += UpdateState;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, (exStyle | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW);
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        RefreshContent(animateWidth: false); // size to the live project's text before positioning
        var wa = WorkArea();
        var st = A.Tracker.State;
        double left = st.PillLeft ?? (wa.Right - Width - 24);
        double top = st.PillTop ?? (wa.Bottom - Height - 24);
        Left = Clamp(left, wa.Left, wa.Right - Width);
        Top = Clamp(top, wa.Top, wa.Bottom - Height);
        _loaded = true;
    }

    // ---------------- live content ----------------

    private void UpdateTime()
    {
        TimeText.Text = A.Tracker.IsRunning ? App.FormatElapsed(A.Tracker.CurrentElapsedSeconds) : "--:--:--";
    }

    private void UpdateState() => RefreshContent(animateWidth: true);

    private void RefreshContent(bool animateWidth)
    {
        bool running = A.Tracker.IsRunning;
        var show = A.Tracker.Active ?? A.Tracker.LastActive;

        Dot.Fill = show is not null ? ColorUtil.Brush(show.Color) : (Brush)FindResource("TextFaint");
        Dot.Opacity = running ? 1.0 : 0.5;
        string label = show?.Code ?? "Idle";
        CodeText.Text = label;
        ToggleBtn.Content = running ? "■" : "▶";
        ToggleBtn.ToolTip = running ? "Stop" : $"Start {show?.Code ?? "project"}";
        UpdateTime();

        _expandedWidth = ComputeExpandedWidth(label, out double codeMaxWidth);
        CodeText.MaxWidth = codeMaxWidth;

        if (!_collapsed)
        {
            if (!_loaded) Width = _expandedWidth;
            else if (_anim == AnimMode.None) AnimateToWidth(_expandedWidth, 220);
            // else: a drag/glide/snap is in progress — it'll pick up the new _expandedWidth
            // next time it settles (Dot_Click / drag release), rather than fighting it now.
        }
    }

    /// <summary>Pill width that comfortably fits the code text + fixed-width timer, clamped to a
    /// sane range; also returns the code text's own trim budget so an extreme name still
    /// ellipsizes cleanly at the ceiling instead of being hard-clipped.</summary>
    private double ComputeExpandedWidth(string code, out double codeMaxWidth)
    {
        double codeW = MeasureTextWidth(code, (FontFamily)FindResource("UiFont"), 13, FontWeights.SemiBold);
        double timeW = MeasureTextWidth(TimeText.Text, (FontFamily)FindResource("MonoFont"), 13, FontWeights.Normal);
        const double chrome = 92 + ShadowPad; // grid margins + dot + body margins + toggle button + shadow padding
        const double gap = 10;    // margin between code and time text

        double natural = chrome + codeW + gap + timeW;
        double clamped = Math.Max(MinExpandedWidth, Math.Min(MaxExpandedWidth, natural));
        codeMaxWidth = clamped >= MaxExpandedWidth
            ? Math.Max(20, MaxExpandedWidth - chrome - gap - timeW)
            : double.PositiveInfinity;
        return clamped;
    }

    private double MeasureTextWidth(string text, FontFamily family, double size, FontWeight weight)
    {
        var typeface = new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal);
        var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            typeface, size, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return ft.WidthIncludingTrailingWhitespace;
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
        _collapsed = !_collapsed;
        Body.Visibility = _collapsed ? Visibility.Collapsed : Visibility.Visible;
        AnimateToWidth(_collapsed ? CollapsedWidth : _expandedWidth, 240);
    }

    /// <summary>Resize toward targetWidth, keeping whichever screen edge is nearer fixed, and
    /// staying fully within the current monitor's work area regardless of direction.</summary>
    private void AnimateToWidth(double targetWidth, double ms)
    {
        var wa = WorkArea();
        bool rightSide = (Left + Width / 2) > (wa.Left + wa.Width / 2);
        double targetLeft = rightSide ? (Left + Width - targetWidth) : Left;
        targetLeft = Clamp(targetLeft, wa.Left + EdgeMargin, wa.Right - targetWidth - EdgeMargin);
        double targetTop = Clamp(Top, wa.Top + EdgeMargin, wa.Bottom - Height - EdgeMargin);

        StartTween(targetLeft, targetTop, targetWidth, ms, () => A.Tracker.SavePillPosition(Left, Top));
    }

    private void Pill_RightClick(object sender, MouseButtonEventArgs e) => A.ShowMainOrTimesheets();

    // ---------------- drag ----------------

    private void Pill_Down(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, Dot) || ToggleBtn.IsMouseOver) return;

        if (e.ClickCount == 2) { A.ShowMainOrTimesheets(); return; } // double-click opens the app (or the timesheet view, if that's what's open)

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
        double instVx = (c.X - _lastPos.X) / dt;
        double instVy = (c.Y - _lastPos.Y) / dt;
        // Smooth over recent samples rather than trusting only the very last one — a real drag
        // gesture naturally decelerates right before the button-up that ends it, so an
        // instantaneous last-sample reading is usually near zero even after a fast fling.
        _vx = _vx * (1 - VelocitySmoothing) + instVx * VelocitySmoothing;
        _vy = _vy * (1 - VelocitySmoothing) + instVy * VelocitySmoothing;
        _lastPos = c; _lastTimeMs = now;

        // Clamp against the FULL virtual desktop (all monitors) while actively dragging, not
        // just the monitor currently under the pill — otherwise the pill hits an invisible wall
        // at that monitor's edge and can never be dragged across to another display.
        var vb = VirtualScreenBounds();
        double nl = Clamp(_winStart.X + (c.X - _cursorStart.X), vb.Left, vb.Right - Width);
        double nt = Clamp(_winStart.Y + (c.Y - _cursorStart.Y), vb.Top, vb.Bottom - Height);
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
    // OS window frame-by-frame, so both the momentum glide and the collapse/expand/content
    // resize are driven by setting Left/Top/Width directly on every CompositionTarget.Rendering
    // tick — the same mechanism the drag itself already uses successfully.

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

    // Real elapsed time per callback — NOT a fixed nominal step. CompositionTarget.Rendering
    // fires at the actual display refresh rate (which can be well above 60Hz), so assuming a
    // fixed ~16ms per callback made the animation run proportionally too fast on faster
    // displays. MinFrameMs filters out a rare near-duplicate composition pass (near-zero real
    // dt); on top of that, the per-callback interval itself has some natural scheduling jitter
    // (a few ms of noise), which — multiplied straight through into a position delta every
    // frame — is enough to be visible as a slight tremor. FrameSmoothing averages that noise
    // out over a handful of frames without masking the display's true refresh rate.
    private const double MinFrameMs = 4.0;  // below any realistic frame interval — treat as a duplicate pass
    private const double MaxFrameMs = 40.0; // clamp so a hitch doesn't cause a big jump
    private const double FrameSmoothing = 0.25; // EMA factor for the measured per-callback interval

    private void OnFrame(object? sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        double rawDt = now - _frameTimeMs;
        if (rawDt < MinFrameMs) return; // spurious extra pass — wait for the next real one
        _frameTimeMs = now;
        rawDt = Math.Min(MaxFrameMs, rawDt);
        _smoothedFrameMs = _smoothedFrameMs * (1 - FrameSmoothing) + rawDt * FrameSmoothing;

        switch (_anim)
        {
            case AnimMode.Glide: StepGlide(_smoothedFrameMs); break;
            case AnimMode.Tween: StepTween(_smoothedFrameMs); break;
        }
    }

    private void StepGlide(double dt)
    {
        // Free-glide across the whole virtual desktop (matches drag behaviour); only the final
        // rest position (once slow enough to hand off to StepTween) is monitor-local.
        var vb = VirtualScreenBounds();
        double nx = Left + _vx * dt;
        double ny = Top + _vy * dt;
        double cx = Clamp(nx, vb.Left, vb.Right - Width);
        double cy = Clamp(ny, vb.Top, vb.Bottom - Height);
        if (cx != nx) _vx = 0;   // hit the outer edge — stop that axis rather than bouncing
        if (cy != ny) _vy = 0;
        Left = cx; Top = cy;

        double decay = Math.Pow(FrictionPerFrame, dt / 16.0);
        _vx *= decay; _vy *= decay;

        if (Math.Abs(_vx) < 0.015 && Math.Abs(_vy) < 0.015)
        {
            var wa = WorkArea(); // now that we've settled, snap within whichever monitor we're on
            var (tx, ty) = SnapTarget(wa);
            double dist = Math.Abs(tx - Left) + Math.Abs(ty - Top);
            StartTween(tx, ty, Width, Clamp(dist * 1.5, 220, 460), () => A.Tracker.SavePillPosition(Left, Top));
        }
    }

    private void StepTween(double dt)
    {
        _tElapsedMs += dt;
        double t = Math.Min(1, _tElapsedMs / _tDurationMs);
        // A gentler tail than cubic (t^2.2 vs t^3): OS window positions are pixel-quantized, so
        // a curve that lingers near-zero velocity right at the end spends longer re-rendering
        // the same rounded pixel before the next one — visible as a stutter right at rest.
        double ease = 1 - Math.Pow(1 - t, 2.2);
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

    /// <summary>Working area (DIP) of the monitor the pill currently sits on — used only for the
    /// at-rest edge/corner snap, so the pill snaps within whichever screen it's now on.</summary>
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

    /// <summary>Bounding box (DIP) of ALL monitors combined — lets the pill be dragged from one
    /// screen to another instead of stopping dead at the edge of whichever one it started on.</summary>
    private Rect VirtualScreenBounds()
    {
        var src = PresentationSource.FromVisual(this);
        var toDip = src?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var vs = System.Windows.Forms.SystemInformation.VirtualScreen;
        var tl = toDip.Transform(new Point(vs.Left, vs.Top));
        var br = toDip.Transform(new Point(vs.Right, vs.Bottom));
        return new Rect(tl, br);
    }

    private static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
}
