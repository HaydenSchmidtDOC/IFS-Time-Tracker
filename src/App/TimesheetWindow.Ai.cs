using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using TimeTracker.App.UI;
using TimeTracker.Core;

namespace TimeTracker.App;

public partial class TimesheetWindow
{
    private const double AiLaneFraction = 0.15;
    private const double AiLaneGap = 3;
    private const double AiAxisW = 46;
    private const double ColPad = 5;

    private bool _aiMenuOpen;

    private bool AiOn => A.Settings.AiTrackingEnabled;
    private double RightGutter => AiOn ? AiAxisW : 0;

    /// <summary>Per-column horizontal layout shared by Totals, Calendar and Project: offsets are
    /// relative to the column's left edge. With AI on, 15% of the usable width is carved off the right.</summary>
    private readonly record struct ColGeom(double HoursLeft, double HoursW, double AiLeft, double AiW)
    {
        public double HoursCx => HoursLeft + HoursW / 2;
    }

    private ColGeom Geom(double colW)
    {
        double usable = Math.Max(20, colW - 2 * ColPad);
        if (!AiOn) return new ColGeom((colW - usable) / 2, usable, 0, 0);
        double aiW = Math.Max(6, usable * AiLaneFraction);
        double hoursW = Math.Max(16, usable - aiW - AiLaneGap);
        double left = (colW - (hoursW + AiLaneGap + aiW)) / 2;
        return new ColGeom(left, hoursW, left + hoursW + AiLaneGap, aiW);
    }

    private bool _aiRenderPending;

    private void InitAi()
    {
        // Statusline pings can arrive many times a second; collapse a burst into one repaint.
        A.AiSessions.Changed += () => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            if (_aiRenderPending) return;
            _aiRenderPending = true;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, () =>
            {
                _aiRenderPending = false;
                if (!IsVisible || SuspendRerender) return;
                RenderCurrentWeek(0);
            });
        });
    }

    private List<AiSession> AiSessionsBetween(DateTime from, DateTime toInclusive)
        => A.AiSessions.Snapshot()
            .Where(s => { var d = AiChartMath.LocalDay(s); return d >= from.Date && d <= toInclusive.Date; })
            .OrderBy(s => s.StartedUtc)
            .ToList();

    private Brush AiFill(AiSession s)
    {
        var p = s.ProjectId is null ? null : A.Tracker.Projects.FirstOrDefault(x => x.Id == s.ProjectId);
        return p is null ? new SolidColorBrush(Color.FromRgb(0x7A, 0x80, 0x8C)) : ColorUtil.Brush(p.Color);
    }

    private string AiTooltip(AiSession s)
    {
        var start = s.StartedUtc.ToLocalTime();
        var end = s.LastSeenUtc.ToLocalTime();
        var proj = string.IsNullOrEmpty(s.ProjectCode) ? "Unassigned" : s.ProjectCode;
        long tokens = s.InputTokens + s.OutputTokens + s.CacheReadTokens + s.CacheWriteTokens;
        return $"{proj} · {AiChartMath.FormatCost(s.CostUsd)}{(s.HasUnpricedUsage ? "+" : "")}\n"
             + $"{s.Provider} · {s.Model ?? "unknown model"} · {s.Endpoint}\n"
             + $"{start:ddd MMM d} {start:HH:mm}–{end:HH:mm}\n"
             + $"{tokens:N0} tokens\nClick to change project";
    }

    private Border AiBlock(AiSession s, double w, double h)
    {
        var b = new Border
        {
            Width = w, Height = h, Background = AiFill(s), CornerRadius = new CornerRadius(3),
            ToolTip = AiTooltip(s), Cursor = Cursors.Hand,
        };
        b.MouseLeftButtonDown += (_, e) => { e.Handled = true; ShowAiReassignMenu(s, b); };
        return b;
    }

    private void ShowAiReassignMenu(AiSession s, FrameworkElement target)
    {
        _aiMenuOpen = true;
        try { AiAssignDialog.Show(s, this); }
        finally { _aiMenuOpen = false; }
    }

    private void DrawCostAxis(Canvas canvas, CostAxis axis, double left, double plotH)
    {
        var faint = (Brush)FindResource("TextFaint");
        var mono = (FontFamily)FindResource("MonoFont");
        int lines = (int)Math.Round(axis.Max / axis.Step);
        for (int i = 0; i <= lines; i++)
        {
            double y = plotH - (i * axis.Step / axis.Max) * plotH;
            var label = new TextBlock
            {
                Text = AiChartMath.FormatCost(i * axis.Step), FontSize = 10.5, Foreground = faint, FontFamily = mono,
                Width = AiAxisW - 6, TextAlignment = TextAlignment.Left,
            };
            Canvas.SetLeft(label, left + 6);
            Canvas.SetTop(label, Math.Max(0, y - 7));
            canvas.Children.Add(label);
            canvas.Children.Add(new Line
            {
                X1 = left, X2 = left + 4, Y1 = y, Y2 = y, Stroke = faint, StrokeThickness = 1, SnapsToDevicePixels = true,
            });
        }
    }

    /// <summary>Stacks sessions bottom-up in the AI lane at <paramref name="left"/>, scaled against
    /// <paramref name="axisMax"/> dollars; gaps are carved out of the stack so its top matches the total.</summary>
    private void AddAiStack(Canvas canvas, IReadOnlyList<AiSession> sessions, double left, double width,
        double plotH, double axisMax)
    {
        if (sessions.Count == 0) return;
        const double gap = 2;
        double total = sessions.Sum(s => s.CostUsd);
        double available = Math.Max(0, total / axisMax * plotH - (sessions.Count - 1) * gap);
        double y = plotH;
        foreach (var s in sessions)
        {
            double h = total > 0 ? Math.Max(2, s.CostUsd / total * available) : 2;
            var b = AiBlock(s, width, h);
            Canvas.SetLeft(b, left);
            Canvas.SetTop(b, y - h);
            canvas.Children.Add(b);
            y -= h + gap;
        }
    }

    private static double MaxStack(IEnumerable<IEnumerable<AiSession>> groups)
        => groups.Select(g => g.Sum(s => s.CostUsd)).DefaultIfEmpty(0).Max();

    /// <summary>Calendar lane: each session spans its start–end time, overlaps packed side by side.</summary>
    private void AddAiCalendarLane(Canvas canvas, List<AiSession> daySessions, DateTime day, double laneLeft,
        double laneW, List<Rect> blockRects)
    {
        if (daySessions.Count == 0) return;
        var spans = daySessions.Select(s => (s.StartedUtc.ToLocalTime(), s.LastSeenUtc.ToLocalTime())).ToList();
        var lanes = AiChartMath.AssignLanes(spans);
        for (int i = 0; i < daySessions.Count; i++)
        {
            var (start, end) = spans[i];
            double startH = start.Date < day ? 0 : start.TimeOfDay.TotalHours;
            double endH = end.Date > day ? 24 : end.TimeOfDay.TotalHours;
            double top = startH * CalendarPxPerHour;
            double h = Math.Max(4, (endH - startH) * CalendarPxPerHour);
            double w = laneW / lanes[i].LaneCount;
            double x = laneLeft + lanes[i].Lane * w;
            var b = AiBlock(daySessions[i], Math.Max(3, w - 1), h);
            Canvas.SetLeft(b, x);
            Canvas.SetTop(b, top);
            canvas.Children.Add(b);
            blockRects.Add(new Rect(x, top, w, h));
        }
    }
}
