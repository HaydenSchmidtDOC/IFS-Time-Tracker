using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using TimeTracker.App.Interop;
using TimeTracker.App.UI;
using TimeTracker.Core;

namespace TimeTracker.App;

/// <summary>
/// A normal, resizable, taskbar-visible window (unlike the rest of the app's borderless
/// popups) so it gets native OS resize/maximize/Snap for free — showing a weekly bar chart of
/// tracked hours, coloured by project, for copying into IFS manually until a real import path
/// is confirmed.
/// </summary>
public partial class TimesheetWindow : Window
{
    private static App A => App.Current;

    private DateTime _weekStart; // Monday of the displayed week, local date
    private FrameworkElement? _currentChart;
    private DateTime _lastWheelNav = DateTime.MinValue;

    public TimesheetWindow()
    {
        InitializeComponent();
        _weekStart = StartOfWeek(DateTime.Today);

        SourceInitialized += (_, _) =>
            DarkTitleBar.Apply(new WindowInteropHelper(this).Handle, !A.IsLightTheme);
        PreviewKeyDown += Window_PreviewKeyDown;

        // The window is reused (Hide/Show), not recreated, across opens — so it needs its own
        // live-update wiring rather than relying on a fresh instance to pick up new state.
        A.Tick += OnTick;
        A.StateChanged += OnStateChanged;
    }

    // A committed block changed the CSV on disk — the cached week no longer reflects it.
    private void OnStateChanged()
    {
        _cachedWeekStart = null;
        if (IsVisible) RenderCurrentWeek(0);
    }

    // Once a second: keep the still-running block's segment growing live. Cheap — reuses the
    // cached committed blocks and only recomputes today's aggregation, no disk I/O.
    private void OnTick()
    {
        if (IsVisible) RenderCurrentWeek(0);
    }

    private static DateTime StartOfWeek(DateTime d)
    {
        int diff = ((int)d.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        return d.Date.AddDays(-diff);
    }

    // ==================== animated open/close ====================
    // The window itself is set to its FINAL bounds immediately, never animated frame-by-frame —
    // repeatedly resizing a window with real OS chrome (title bar, resize border) via native
    // SetWindowPos is heavy (DWM recomposites the whole frame each step) and reads as jittery,
    // unlike the borderless pill this app also animates. Instead, the "grow"/"shrink" illusion
    // is a pure RenderTransform (scale + translate) + Opacity on the content — fully
    // GPU-composited, no native resize involved at all.

    /// <summary>Grow from origin's exact bounds to a comfortable size centred on it.</summary>
    public void AnimateOpenFrom(Window origin)
    {
        var wa = WorkAreaFor(origin);
        double targetW = Math.Min(980, wa.Width - 80);
        double targetH = Math.Min(680, wa.Height - 80);
        double targetLeft = Clamp(origin.Left + origin.Width / 2 - targetW / 2, wa.Left + 16, wa.Right - targetW - 16);
        double targetTop = Clamp(origin.Top + origin.Height / 2 - targetH / 2, wa.Top + 16, wa.Bottom - targetH - 16);
        Left = targetLeft; Top = targetTop; Width = targetW; Height = targetH;

        // Start the transform so the content appears at origin's screen rect, then animate to identity.
        double sx = origin.Width / targetW, sy = origin.Height / targetH;
        double tx = origin.Left - targetLeft, ty = origin.Top - targetTop;
        ScaleXform.ScaleX = sx; ScaleXform.ScaleY = sy;
        TranslateXform.X = tx; TranslateXform.Y = ty;
        Opacity = 0;

        Show();
        Activate();

        var dur = TimeSpan.FromMilliseconds(320);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ScaleXform.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(sx, 1, dur) { EasingFunction = ease });
        ScaleXform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(sy, 1, dur) { EasingFunction = ease });
        TranslateXform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(tx, 0, dur) { EasingFunction = ease });
        TranslateXform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(ty, 0, dur) { EasingFunction = ease });
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)));

        _weekStart = StartOfWeek(DateTime.Today);
        // The window is reused across opens, so a stale cache from the last time it was open
        // (possibly still keyed to this same week) must not survive a reopen — otherwise
        // whatever got tracked while it was closed wouldn't show up until the cache happened
        // to be invalidated some other way (navigating weeks, deleting a block).
        _cachedWeekStart = null;
        RenderCurrentWeek(0);
    }

    /// <summary>Shrink back toward target's current bounds, then hide and hand control back.</summary>
    public void AnimateCloseTo(Window target, Action onDone)
    {
        double sx = target.Width / Width, sy = target.Height / Height;
        double tx = target.Left - Left, ty = target.Top - Top;

        var dur = TimeSpan.FromMilliseconds(260);
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(240));
        fade.Completed += (_, _) =>
        {
            Hide();
            // Reset so the next AnimateOpenFrom starts clean.
            ScaleXform.BeginAnimation(ScaleTransform.ScaleXProperty, null); ScaleXform.ScaleX = 1;
            ScaleXform.BeginAnimation(ScaleTransform.ScaleYProperty, null); ScaleXform.ScaleY = 1;
            TranslateXform.BeginAnimation(TranslateTransform.XProperty, null); TranslateXform.X = 0;
            TranslateXform.BeginAnimation(TranslateTransform.YProperty, null); TranslateXform.Y = 0;
            onDone();
        };

        ScaleXform.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, sx, dur) { EasingFunction = ease });
        ScaleXform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, sy, dur) { EasingFunction = ease });
        TranslateXform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, tx, dur) { EasingFunction = ease });
        TranslateXform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, ty, dur) { EasingFunction = ease });
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Working area (DIP) of whichever monitor window w is currently centred on.
    /// Internal+static so App can reuse it to reposition the main window on close — see
    /// AnimateCloseTo's caller in App.CloseTimesheets.</summary>
    internal static Rect WorkAreaFor(Window w)
    {
        var src = PresentationSource.FromVisual(w);
        var toDip = src?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var toDev = src?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var centerDev = toDev.Transform(new Point(w.Left + w.Width / 2, w.Top + w.Height / 2));
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)centerDev.X, (int)centerDev.Y));
        var wa = screen.WorkingArea;
        var tl = toDip.Transform(new Point(wa.Left, wa.Top));
        var br = toDip.Transform(new Point(wa.Right, wa.Bottom));
        return new Rect(tl, br);
    }

    internal static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (A.IsQuitting) return;
        e.Cancel = true;
        A.CloseTimesheets();
    }

    // ==================== custom title bar controls ====================
    // Close routes through this.Close() -> Window_Closing -> A.CloseTimesheets(), so it (and
    // Alt+F4, and the taskbar's own right-click Close) all animate back to the main window
    // exactly like every other close path, rather than special-casing this button.

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaxRestore_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_StateChanged(object? sender, EventArgs e)
        => MaxRestoreBtn.Content = WindowState == WindowState.Maximized ? "❐" : "▢";

    // ==================== week navigation ====================

    private void PrevWeek_Click(object sender, RoutedEventArgs e) => Navigate(-1);
    private void NextWeek_Click(object sender, RoutedEventArgs e) => Navigate(1);

    private void Navigate(int weeks)
    {
        _weekStart = _weekStart.AddDays(7 * weeks);
        RenderCurrentWeek(weeks);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Left) { Navigate(-1); e.Handled = true; }
        else if (e.Key == Key.Right) { Navigate(1); e.Handled = true; }
    }

    // A trackpad swipe fires many small wheel events in quick succession (unlike a mouse's
    // discrete clicks), so without a cooldown a single gesture blew through several weeks at
    // once. This lets through only the first event per gesture, roughly matching how long the
    // slide animation itself takes.
    private const int WheelCooldownMs = 380;

    private void ChartArea_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        var now = DateTime.UtcNow;
        if ((now - _lastWheelNav).TotalMilliseconds < WheelCooldownMs) return;
        _lastWheelNav = now;
        Navigate(e.Delta < 0 ? 1 : -1); // one notch = one week, snapped — not a continuous scroll
    }

    private void ChartCard_SizeChanged(object sender, SizeChangedEventArgs e) => RenderCurrentWeek(0);

    // ==================== data + chart rendering ====================

    // Caches the currently-displayed week's blocks so a pure resize (which re-renders the chart
    // at new pixel dimensions but doesn't change *which* week is shown) doesn't re-read and
    // re-parse the CSV file(s) from disk on every SizeChanged tick. Invalidated whenever the
    // displayed week actually changes.
    private DateTime? _cachedWeekStart;
    private List<TimeBlock>? _cachedBlocks;

    /// <summary>Opens the day-detail popup; a deletion made there invalidates the week cache
    /// (the CSV changed underneath us) and re-renders in place.</summary>
    private void OpenDayDetail(DateTime day)
    {
        var w = new DayBlocksWindow(day, () =>
        {
            _cachedWeekStart = null;
            RenderCurrentWeek(0);
        })
        { Owner = this };
        w.ShowDialog();
    }

    private List<TimeBlock> GetWeekBlocks()
    {
        if (_cachedWeekStart == _weekStart && _cachedBlocks is not null) return _cachedBlocks;
        _cachedBlocks = A.Log.ReadRange(_weekStart, _weekStart.AddDays(6));
        _cachedWeekStart = _weekStart;
        return _cachedBlocks;
    }

    private void RenderCurrentWeek(int slideDirection)
    {
        WeekLabel.Text = FormatWeekLabel(_weekStart);

        // Raw blocks per day (chronological), not pre-aggregated — grouping into bar segments
        // depends on the merge-mode setting (see BuildDaySegments) and needs the actual session
        // boundaries and notes to do that, not just a per-project total.
        var byDay = new Dictionary<DateTime, List<TimeBlock>>();
        for (int i = 0; i < 7; i++) byDay[_weekStart.AddDays(i)] = new List<TimeBlock>();

        double weekTotal = 0;
        foreach (var b in GetWeekBlocks())
        {
            var day = b.StartLocal.Date;
            if (!byDay.TryGetValue(day, out var list)) continue;
            list.Add(b);
            weekTotal += b.DurationHours;
        }
        foreach (var list in byDay.Values) list.Sort((a, b) => a.StartLocal.CompareTo(b.StartLocal));

        // Fold in the still-running block as a real (if synthetic/unsaved) TimeBlock — it isn't
        // written to the CSV until it ends, but the chart should reflect what's actually
        // happening right now. Building it as an actual TimeBlock, appended chronologically last,
        // means grouping/ordering/folding all treat it exactly like a committed one with no
        // special-casing beyond identifying which block IS the live one (see BuildDaySegments).
        var today = DateTime.Today;
        TimeBlock? liveBlock = null;
        // byDay only has keys for the currently-displayed week — today isn't one of them once
        // you've navigated away from the current week, so this must check rather than index
        // directly (byDay[today] threw KeyNotFoundException and crashed the app on any scroll
        // away from the current week while a project was actively tracking).
        if (A.Tracker.IsRunning && A.Tracker.Active is { } active && A.Tracker.State.BlockStartUtc is DateTime startUtc
            && byDay.TryGetValue(today, out var todayBlocks))
        {
            liveBlock = new TimeBlock
            {
                ProjectCode = active.Code, Asn = active.Asn, ProjectName = active.Name,
                StartLocal = startUtc.ToLocalTime(), EndLocal = DateTime.Now,
                DurationSeconds = A.Tracker.CurrentElapsedSeconds, Notes = "",
            };
            todayBlocks.Add(liveBlock);
            // Raw (unrounded) hours so the live segment grows smoothly every tick, rather than
            // in the 6-minute jumps DurationHours' 0.1h rounding would otherwise produce.
            weekTotal += liveBlock.DurationSeconds / 3600.0;
        }
        WeekTotalText.Text = $"{weekTotal:0.0} h";

        double w = ChartCard.ActualWidth > 40 ? ChartCard.ActualWidth - 40 : 820;
        double h = ChartCard.ActualHeight > 40 ? ChartCard.ActualHeight - 40 : 440;
        SlideIn(BuildChart(byDay, liveBlock, w, h, today), slideDirection);
        RebuildLegend(byDay);
    }

    /// <summary>Hours contributed by one block to a chart total — the live block uses raw
    /// (unrounded) elapsed time so its segment grows smoothly every tick; committed blocks use
    /// the same 0.1h-rounded DurationHours the CSV/export already show.</summary>
    private static double HoursOf(TimeBlock b, TimeBlock? liveBlock)
        => ReferenceEquals(b, liveBlock) ? b.DurationSeconds / 3600.0 : b.DurationHours;

    /// <summary>Blend a colour toward white by <paramref name="amount"/> (0..1) — used for the
    /// live segment's border, a brighter tint of its own fill rather than an unrelated colour.</summary>
    private static Color Lighten(Color c, double amount)
    {
        byte L(byte ch) => (byte)Math.Min(255, ch + (255 - ch) * amount);
        return Color.FromRgb(L(c.R), L(c.G), L(c.B));
    }

    private static (double axisMax, double step) ComputeAxisScale(double rawMax)
    {
        double axisMax = Math.Max(4, Math.Ceiling(rawMax / 2.0) * 2.0);
        return (axisMax, axisMax / 4.0);
    }

    private static string FormatWeekLabel(DateTime weekStart)
    {
        // NB: a lone "d" is a *standard* format specifier (short date) in .NET, not "day
        // number", even embedded in a larger interpolated string — "%d" forces the custom
        // (day-of-month-only) interpretation instead.
        var end = weekStart.AddDays(6);
        if (weekStart.Year != end.Year) return $"{weekStart:MMM d, yyyy} – {end:MMM d, yyyy}";
        return weekStart.Month == end.Month
            ? $"{weekStart:MMM d} – {end:%d}, {end:yyyy}"
            : $"{weekStart:MMM d} – {end:MMM d}, {end:yyyy}";
    }

    /// <summary>One drawable bar segment — either a real project, or a folded "Other" bucket
    /// (Asn null). Display-only grouping; never touches recorded data (see
    /// Settings.ChartMinSegmentHours).</summary>
    private sealed record ChartSegment(string Label, string? Asn, double Hours, Brush Fill, string Tooltip, DateTime OrderKey, bool IsLive = false);

    /// <summary>A run of one or more blocks accumulated into a single candidate segment, before
    /// the below-threshold ones get folded into "Other".</summary>
    private sealed class SessionGroup
    {
        public required string ProjectCode;
        public double Hours;
        public DateTime Latest = DateTime.MinValue;
        public bool ContainsLive;
        public string? Note;
    }

    /// <summary>
    /// Groups a day's blocks into candidate segments, then folds anything under the configured
    /// threshold into one neutral "Other" segment (its tooltip lists the exact breakdown, so
    /// nothing is actually hidden — just visually consolidated). Ordered by each segment's
    /// most-recent activity (ascending), so the stack builds bottom-up with the most-recently-
    /// touched one ending up on top. liveBlock (only meaningful for today) is exempt from
    /// folding — hiding the currently-recording session inside "Other" would defeat the point of
    /// highlighting it as live.
    ///
    /// Grouping itself follows Settings.ChartMergeAllSessions: when true, every block for a
    /// project that day sums into one segment regardless of gaps or notes (a project worked at
    /// 8am and again at 2pm still ends up as a single bar). When false, only strictly
    /// back-to-back blocks (nothing else logged between them) sharing the same project and note
    /// collapse together — touching the same project again later, or with a different note,
    /// starts a fresh segment of its own.
    /// </summary>
    private List<ChartSegment> BuildDaySegments(List<TimeBlock> dayBlocks, IReadOnlyList<Project> projects,
        Brush otherFill, TimeBlock? liveBlock)
    {
        double minHours = A.Settings.ChartMinSegmentHours;
        var groups = new List<SessionGroup>();

        if (A.Settings.ChartMergeAllSessions)
        {
            var byCode = new Dictionary<string, SessionGroup>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in dayBlocks)
            {
                if (!byCode.TryGetValue(b.ProjectCode, out var g))
                {
                    g = new SessionGroup { ProjectCode = b.ProjectCode };
                    byCode[b.ProjectCode] = g;
                    groups.Add(g);
                }
                g.Hours += HoursOf(b, liveBlock);
                if (b.EndLocal > g.Latest) g.Latest = b.EndLocal;
                if (ReferenceEquals(b, liveBlock)) g.ContainsLive = true;
            }
        }
        else
        {
            SessionGroup? cur = null;
            foreach (var b in dayBlocks) // already sorted chronologically by the caller
            {
                string note = b.Notes ?? "";
                bool continuesRun = cur is not null
                    && string.Equals(cur.ProjectCode, b.ProjectCode, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(cur.Note, note, StringComparison.Ordinal);
                if (!continuesRun)
                {
                    cur = new SessionGroup { ProjectCode = b.ProjectCode, Note = note };
                    groups.Add(cur);
                }
                cur!.Hours += HoursOf(b, liveBlock);
                if (b.EndLocal > cur.Latest) cur.Latest = b.EndLocal;
                if (ReferenceEquals(b, liveBlock)) cur.ContainsLive = true;
            }
        }

        var kept = new List<ChartSegment>();
        var folded = new List<(string Code, double Hours)>();
        double foldedTotal = 0;
        DateTime foldedLatest = DateTime.MinValue;

        foreach (var g in groups)
        {
            if (g.Hours <= 0) continue;
            var p = projects.FirstOrDefault(x => string.Equals(x.Code, g.ProjectCode, StringComparison.OrdinalIgnoreCase));
            if (p is null) continue; // project since deleted from the list; historical data stays in the CSV

            if (g.Hours < minHours && !g.ContainsLive)
            {
                folded.Add((g.ProjectCode, g.Hours));
                foldedTotal += g.Hours;
                if (g.Latest > foldedLatest) foldedLatest = g.Latest;
                continue;
            }

            string tooltip = g.ContainsLive
                ? $"{p.Code} · ASN {p.Asn} · {g.Hours:0.0} h · recording now"
                : $"{p.Code} · ASN {p.Asn} · {g.Hours:0.0} h";
            if (!string.IsNullOrWhiteSpace(g.Note)) tooltip += $"\nNote: {g.Note}";

            kept.Add(new ChartSegment(
                Label: $"{p.Code} · {g.Hours:0.#}h",
                Asn: p.Asn,
                Hours: g.Hours,
                Fill: ColorUtil.Brush(p.Color),
                Tooltip: tooltip,
                OrderKey: g.Latest,
                IsLive: g.ContainsLive));
        }

        if (folded.Count > 0)
        {
            var breakdown = string.Join("\n", folded.OrderByDescending(f => f.Hours).Select(f => $"{f.Code}: {f.Hours:0.0} h"));
            kept.Add(new ChartSegment(
                Label: $"Other · {foldedTotal:0.#}h",
                Asn: null,
                Hours: foldedTotal,
                Fill: otherFill,
                Tooltip: $"Other ({folded.Count} entr{(folded.Count == 1 ? "y" : "ies")}) · {foldedTotal:0.0} h\n{breakdown}",
                OrderKey: foldedLatest));
        }

        return kept.OrderBy(s => s.OrderKey).ToList();
    }

    private FrameworkElement BuildChart(Dictionary<DateTime, List<TimeBlock>> byDay, TimeBlock? liveBlock,
        double width, double height, DateTime today)
    {
        var projects = A.Tracker.Projects;
        double rawMax = byDay.Values.Select(day => day.Sum(b => HoursOf(b, liveBlock))).DefaultIfEmpty(0).Max();
        var (axisMax, step) = ComputeAxisScale(rawMax);

        const double yAxisW = 40;
        const double xAxisH = 32;
        double plotW = Math.Max(80, width - yAxisW);
        double plotH = Math.Max(60, height - xAxisH);

        var canvas = new Canvas { Width = width, Height = height };
        var gridBrush = (Brush)FindResource("Border");
        var faintBrush = (Brush)FindResource("TextFaint");
        var dimBrush = (Brush)FindResource("TextDim");
        var accentBrush = (Brush)FindResource("Accent");
        var monoFont = (FontFamily)FindResource("MonoFont");

        int lines = (int)Math.Round(axisMax / step);
        for (int i = 0; i <= lines; i++)
        {
            double hoursAt = i * step;
            double y = plotH - (hoursAt / axisMax) * plotH;
            canvas.Children.Add(new Line
            {
                X1 = yAxisW, X2 = width, Y1 = y, Y2 = y,
                Stroke = gridBrush, StrokeThickness = 1, SnapsToDevicePixels = true,
            });
            var label = new TextBlock
            {
                Text = $"{hoursAt:0}h", FontSize = 10.5, Foreground = faintBrush, FontFamily = monoFont,
                Width = yAxisW - 8, TextAlignment = TextAlignment.Right,
            };
            Canvas.SetLeft(label, 0);
            Canvas.SetTop(label, Math.Max(0, y - 7));
            canvas.Children.Add(label);
        }

        var days = byDay.Keys.OrderBy(d => d).ToList();
        double dayW = plotW / 7.0;
        double barW = Math.Max(20, dayW - 10); // fill most of the day column, Teams-style

        for (int i = 0; i < days.Count; i++)
        {
            var day = days[i];
            double colLeft = yAxisW + dayW * i;
            double cx = colLeft + dayW / 2;

            if (day == today)
            {
                // A faint accent tint, not a solid block — enough to mark "today" without
                // reading as a rendering glitch next to a small data bar.
                var ac = ((SolidColorBrush)accentBrush).Color;
                var hl = new Rectangle
                {
                    Width = dayW - 4, Height = plotH, RadiusX = 6, RadiusY = 6,
                    Fill = new SolidColorBrush(Color.FromArgb(20, ac.R, ac.G, ac.B)),
                };
                Canvas.SetLeft(hl, colLeft + 2);
                Canvas.SetTop(hl, 0);
                canvas.Children.Add(hl);
            }

            // Stack bottom-up ordered by each segment's most-recent activity that day (folded
            // "Other" entries use their latest constituent's activity) — so the most-recently-
            // touched one (including the live one, whose "activity" is always right now) ends
            // up on top.
            var segments = BuildDaySegments(byDay[day], projects, otherFill: faintBrush,
                liveBlock: day == today ? liveBlock : null);

            double yCursor = plotH;
            const double segGap = 2; // a small gap between stacked segments reads less "blocky"
            foreach (var seg0 in segments)
            {
                double segH = Math.Max(2, seg0.Hours / axisMax * plotH);
                var baseColor = ((SolidColorBrush)seg0.Fill).Color;

                var seg = new Border
                {
                    Width = barW, Height = segH,
                    Background = seg0.Fill,
                    CornerRadius = new CornerRadius(4),
                    ToolTip = seg0.Tooltip,
                    ClipToBounds = true,
                };
                // The still-running segment gets its own outline regardless of height — a border
                // reads even on a 2px-tall sliver, unlike text, so it's the primary "this one's
                // live" cue; the pulsing dot next to the label below adds further reinforcement
                // once there's room for it. The border is a brighter tint of the segment's OWN
                // colour, not the unrelated red "Live" accent — an orange/red ring around e.g. a
                // green bar read as a mismatched box slapped on top rather than a highlighted
                // version of the same project.
                if (seg0.IsLive)
                {
                    var liveBorder = new SolidColorBrush(Lighten(baseColor, 0.4));
                    seg.BorderBrush = liveBorder;
                    seg.BorderThickness = new Thickness(2);
                    liveBorder.BeginAnimation(SolidColorBrush.OpacityProperty, new DoubleAnimation(0.55, 1.0, TimeSpan.FromMilliseconds(900))
                    { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
                }

                var content = new Grid();

                // Only label the segment in-place if it's tall enough to hold readable text;
                // short segments keep the tooltip as their only label. Hours always pairs with
                // the code (the number that matters most); ASN (when there is one — "Other"
                // has none) only shows up once there's room for a second line. Anchored to the
                // top edge rather than centred, so it doesn't drift as the segment's height
                // changes from one render to the next.
                if (segH >= 20)
                {
                    var ink = new SolidColorBrush(SystemAccent.ReadableInk(baseColor));
                    var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6, 4, 6, 2) };

                    if (seg0.IsLive)
                    {
                        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
                        titleRow.Children.Add(new TextBlock
                        {
                            Text = seg0.Label, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = ink,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        });
                        var dot = new TextBlock
                        {
                            Text = "  ●", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = ink,
                        };
                        // A small pulsing dot next to the label — not a full-surface texture —
                        // is the "still recording" cue, matching how the rest of the app marks
                        // "live" (the REC badge, the switcher's LIVE row) with a small accent
                        // glyph rather than dressing up the whole surface. One-shot rather than
                        // looped: this window already rebuilds the live segment every second to
                        // grow it, so a self-looping animation would restart out of sync anyway —
                        // the redraw itself is what makes it "breathe".
                        dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, 0.3, TimeSpan.FromMilliseconds(900))
                        { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
                        titleRow.Children.Add(dot);
                        labels.Children.Add(titleRow);
                    }
                    else
                    {
                        labels.Children.Add(new TextBlock
                        {
                            Text = seg0.Label, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = ink,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        });
                    }

                    if (segH >= 36 && seg0.Asn is not null)
                    {
                        labels.Children.Add(new TextBlock
                        {
                            Text = $"ASN {seg0.Asn}", FontSize = 9, Foreground = ink, Opacity = 0.85,
                            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0),
                        });
                    }
                    content.Children.Add(labels);
                }

                seg.Child = content;
                Canvas.SetLeft(seg, cx - barW / 2);
                Canvas.SetTop(seg, yCursor - segH);
                canvas.Children.Add(seg);
                yCursor -= segH + segGap;
            }

            // Clickable — opens the day's individual blocks (view/delete). Kept off the bars
            // themselves so each segment's own hover tooltip isn't swallowed by a click target.
            var labelStack = new StackPanel
            {
                Width = dayW, Cursor = System.Windows.Input.Cursors.Hand,
                Background = Brushes.Transparent, // needed for hit-testing on an otherwise-empty area
                ToolTip = "View this day's blocks",
            };
            labelStack.Children.Add(new TextBlock
            {
                Text = day.ToString("ddd"), FontSize = 11.5, TextAlignment = TextAlignment.Center,
                FontWeight = day == today ? FontWeights.Bold : FontWeights.Medium,
                Foreground = day == today ? accentBrush : dimBrush,
            });
            labelStack.Children.Add(new TextBlock
            {
                // %d forces "day number" — a lone "d" is a *standard* short-date specifier (see
                // FormatWeekLabel) and was rendering the full date here, not just the day.
                Text = day.ToString("%d"), FontSize = 9.5, TextAlignment = TextAlignment.Center, Foreground = faintBrush,
            });
            var dayForClick = day;
            labelStack.MouseLeftButtonUp += (_, _) => OpenDayDetail(dayForClick);
            Canvas.SetLeft(labelStack, colLeft);
            Canvas.SetTop(labelStack, plotH + 4);
            canvas.Children.Add(labelStack);
        }

        return canvas;
    }

    /// <summary>Add the new week's chart, sliding it in while the previous one slides out and fades.</summary>
    private void SlideIn(FrameworkElement newVisual, int direction)
    {
        var old = _currentChart;
        _currentChart = newVisual;

        double dist = Math.Max(120, ChartCard.ActualWidth) * 0.25;
        var tt = new TranslateTransform(direction == 0 ? 0 : direction * dist, 0);
        newVisual.RenderTransform = tt;
        newVisual.Opacity = direction == 0 ? 1 : 0;
        ChartHost.Children.Add(newVisual);

        if (direction != 0)
        {
            tt.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(tt.X, 0, TimeSpan.FromMilliseconds(280)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            newVisual.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)));
        }

        if (old is null) return;

        if (direction == 0)
        {
            ChartHost.Children.Remove(old);
            return;
        }

        var oldTt = new TranslateTransform(0, 0);
        old.RenderTransform = oldTt;
        var slideOut = new DoubleAnimation(0, -direction * dist, TimeSpan.FromMilliseconds(280))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        slideOut.Completed += (_, _) => ChartHost.Children.Remove(old);
        oldTt.BeginAnimation(TranslateTransform.XProperty, slideOut);
        old.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(240)));
    }

    private void RebuildLegend(Dictionary<DateTime, List<TimeBlock>> byDay)
    {
        Legend.Children.Clear();
        var active = new HashSet<string>(byDay.Values.SelectMany(d => d.Select(b => b.ProjectCode)), StringComparer.OrdinalIgnoreCase);

        foreach (var p in A.Tracker.Projects)
        {
            if (!active.Contains(p.Code)) continue;
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 4) };
            item.Children.Add(new Ellipse
            {
                Width = 10, Height = 10, Fill = ColorUtil.Brush(p.Color),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
            });
            item.Children.Add(new TextBlock
            {
                Text = p.Code, FontSize = 12, Foreground = (Brush)FindResource("TextDim"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            Legend.Children.Add(item);
        }

        if (Legend.Children.Count == 0)
        {
            Legend.Children.Add(new TextBlock
            {
                Text = "No tracked time this week.", FontSize = 12, Foreground = (Brush)FindResource("TextFaint"),
            });
        }
    }
}
