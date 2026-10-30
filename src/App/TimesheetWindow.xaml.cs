using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
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

    /// <summary>Which of the two timesheet renderers is showing. Every switch (see the header
    /// toggle's SelectionChanged below) writes back to Settings.DefaultTimesheetView, and
    /// AnimateOpenFrom reads it back on each fresh open — so the window just resumes on
    /// whichever view was last used rather than a fixed, separately-configured default.</summary>
    private enum ViewMode { Bar, Calendar, Project }
    private ViewMode _viewMode;
    private DateTime _monthStart; // first of the displayed month, Project view only
    private double _projectScrollOffset; // Project view's horizontal scroll, kept across rebuilds

    private static ViewMode ModeFromSetting(string s)
        => s == "Calendar" ? ViewMode.Calendar : s == "Project" ? ViewMode.Project : ViewMode.Bar;
    private static string ModeToSetting(ViewMode m)
        => m == ViewMode.Calendar ? "Calendar" : m == ViewMode.Project ? "Project" : "Bar";
    private static int ModeIndex(ViewMode m) => (int)m;
    private static DateTime StartOfMonth(DateTime d) => new(d.Year, d.Month, 1);
    private readonly SegmentedToggle _viewToggle;

    // Calendar view remembers its own scroll offset across the once-a-second rebuild (see
    // OnTick) so a live block's growth doesn't keep yanking the user back to the top — but resets
    // to null (re-centering on the work-hours default) every time the view is freshly switched
    // INTO, per SelectionChanged below, rather than persisting indefinitely.
    private double? _calendarScrollOffset;

    /// <summary>
    /// Live state for an in-progress calendar-view drag (edge-resize or whole-block move) —
    /// see BuildCalendar's drag-handling section, where it's created/consumed. Null whenever
    /// nothing is being dragged. Instance-level (not local to BuildCalendar) because it has to
    /// survive between separate MouseDown/MouseMove/MouseUp event callbacks, which the canvas-
    /// level handlers below check on every tick/resize/state-change to avoid a full rebuild
    /// ripping the dragged element out from under an active mouse capture.
    /// </summary>
    private sealed class DragState
    {
        public required bool IsResize;
        public required bool ResizeTop; // only meaningful when IsResize
        public required TimeBlock Block;
        public required Border Element; // mutated live (Canvas.Top/Height/Left) as the mouse moves
        /// <summary>The day this block actually belongs to on disk — fixed for the whole drag,
        /// used to locate/exclude its own row. For a whole-block move, that's not necessarily the
        /// day it's CURRENTLY hovering over any more — see CurrentDay.</summary>
        public required DateTime Day;
        public required double StartMouseY;
        public required double OrigTop, OrigHeight;
        /// <summary>Clamp range for the value actually being dragged: newTop for a top-edge
        /// resize or a whole-block move, newBottom for a bottom-edge resize.</summary>
        public required double LowBound, HighBound;
        // Edge-resize only: the one immediate neighbour whose facing edge tracks along — never
        // the live block (that's treated as a hard stop instead, see BeginResize).
        public TimeBlock? Neighbor;
        public Border? NeighborElement;
        public double NeighborOrigTop, NeighborOrigHeight;
        /// <summary>The neighbour is one piece of a midnight-crossing row, so it can be pushed
        /// down to nothing here (the rest of the row lives on the other day).</summary>
        public bool NeighborCanVanish;

        // Whole-block move only (IsResize false) — lets a block be dragged to a different day
        // column, not just up/down within its own. Resize never touches these.
        public double StartMouseX;
        public double OrigLeft;
        /// <summary>Which day column the block is CURRENTLY hovering over, live-updated on every
        /// MouseMove as it crosses column boundaries — starts equal to Day, may end up different
        /// by MouseUp.</summary>
        public DateTime CurrentDay;
    }
    private DragState? _drag;

    // Fixed reference point every breathing animation's phase is computed against — see
    // BreathingAnimation. Doesn't need to mean anything beyond "process start"; it only matters
    // that every call site measures phase against the SAME instant.
    private static readonly DateTime AnimationEpoch = DateTime.UtcNow;

    /// <summary>
    /// A continuous, phase-locked autoreverse pulse from <paramref name="from"/> to <paramref
    /// name="to"/> and back, every <paramref name="half"/> each way.
    ///
    /// The live block's canvas is fully rebuilt from scratch every second (see OnTick) to grow
    /// it — so its "still recording" pulse (a border/dot opacity animation) was being
    /// re-triggered from scratch each time too, restarting at `from` every ~1s. Since the
    /// one-shot ramp (900ms) doesn't evenly divide the ~1000ms rebuild interval, that produced a
    /// visible snap back to the dim/faint end of the range at the start of each new cycle instead
    /// of a smooth breathe — reported as the live indicator "growing then cutting back to
    /// nothing". Fixing it doesn't mean making the animation not restart (it has to — the
    /// element itself is brand new); it means giving each new instance a BeginTime far enough in
    /// the past that a Forever/AutoReverse timeline picks up exactly where the LAST instance's
    /// would be right now, so consecutive instances read as one uninterrupted breathing loop.
    /// </summary>
    private void Breathe(IAnimatable target, DependencyProperty prop, double from, double to)
    {
        var clock = BreathingAnimation(from, to, TimeSpan.FromMilliseconds(900)).CreateClock();
        target.ApplyAnimationClock(prop, clock);
        _breathing.Add((target, prop, clock));
    }

    /// <summary>Stop every live pulse. A Forever clock keeps WPF rendering at full frame rate for
    /// as long as it runs — even on elements a rebuild has already discarded, or while this window
    /// is hidden/minimised — and that constant frame churn starved the pill's and main window's own
    /// repaints down to one every few seconds. Detaching the clock from its property isn't enough
    /// (it stays in the timing tree, still ticking, until collected), so it's removed outright.
    /// Pulses are stopped on every full rebuild (the new render starts its own) and whenever the
    /// window isn't on screen.</summary>
    private void StopBreathing()
    {
        foreach (var (target, prop, clock) in _breathing)
        {
            clock.Controller?.Remove();
            target.ApplyAnimationClock(prop, null);
        }
        _breathing.Clear();
    }

    private readonly List<(IAnimatable target, DependencyProperty prop, AnimationClock clock)> _breathing = new();

    /// <summary>Hidden (closed — the window is reused) or minimised: nothing here is on screen.</summary>
    private bool Offscreen => !IsVisible || WindowState == WindowState.Minimized;

    private static DoubleAnimation BreathingAnimation(double from, double to, TimeSpan half)
    {
        double cycleMs = half.TotalMilliseconds * 2;
        double phaseMs = (DateTime.UtcNow - AnimationEpoch).TotalMilliseconds % cycleMs;
        var anim = new DoubleAnimation(from, to, half)
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = TimeSpan.FromMilliseconds(-phaseMs),
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        // A slow opacity breathe reads the same at 12fps as at 60, and WPF renders every window on
        // this thread at the fastest rate any running clock asks for — uncapped, the pulse kept
        // the whole app (pill included) redrawing at ~60fps while Timesheets was open.
        Timeline.SetDesiredFrameRate(anim, 12);
        return anim;
    }

    /// <summary>True while a calendar-view delete confirmation ("Delete this block?") is showing.
    /// Same rationale as _drag: the whole canvas rebuilds every second while a live session
    /// ticks, and that popup is a plain child of THIS render's canvas — an untracked rebuild
    /// would silently wipe it out from under the user before they can click either button.</summary>
    private bool _confirmingDelete;

    /// <summary>True while the calendar view's drag-to-add gesture (empty-space click-drag, as
    /// opposed to _drag's move/resize of an EXISTING block) is in progress — same rationale as
    /// _drag/_confirmingDelete: the highlight rectangle it draws is a plain child of this
    /// render's canvas, so an untracked rebuild mid-drag would tear it out from under the mouse
    /// capture. See BuildCalendar's own drag-to-add section for the actual gesture.</summary>
    private bool _addDragging;

    /// <summary>Whether an interactive, in-progress calendar-view gesture owns the canvas right
    /// now — the shared guard against the once-a-second/resize/state-change auto-rebuilds
    /// tearing it out from under the user (see _drag, _confirmingDelete, _addDragging).</summary>
    private bool SuspendRerender => _drag is not null || _confirmingDelete || _addDragging || _aiMenuOpen || _dialogOpen;

    /// <summary>True while an AddTimeDialog opened from here is up. Its modal loop keeps the
    /// per-second tick (and AI/state refreshes) running, and any of those that falls back to a full
    /// RenderCurrentWeek would rebuild the canvas underneath the mouse handler that opened the
    /// dialog. Every caller re-renders itself once the dialog returns.</summary>
    private bool _dialogOpen;

    private bool ShowTimeDialog(Func<bool> show)
    {
        _dialogOpen = true;
        try { return show(); }
        finally { _dialogOpen = false; }
    }

    public TimesheetWindow()
    {
        InitializeComponent();
        _weekStart = StartOfWeek(DateTime.Today);
        _monthStart = StartOfMonth(DateTime.Today);
        _viewMode = ModeFromSetting(A.Settings.DefaultTimesheetView);

        _viewToggle = new SegmentedToggle(new[] { "Totals", "Calendar", "Project" }, ModeIndex(_viewMode), fontSize: 11.5);
        _viewToggle.SelectionChanged += ApplyViewModeChange;
        ViewToggleHost.Content = _viewToggle.Root;
        InitTimesheetSettingsPopover();
        UpdateMergeToggleVisibility(animate: false);

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            DarkTitleBar.Apply(hwnd, !A.IsLightTheme);
            TaskbarMinimizeFix.Apply(hwnd);
        };
        PreviewKeyDown += Window_PreviewKeyDown;
        // TimesheetSettingsPopup is StaysOpen="True" now (see its XAML remarks) — WPF no longer
        // auto-closes it on deactivation the way a StaysOpen="False" popup would (e.g. opening
        // AddTimeDialog, which owns this window, or Alt-Tabbing away), so that has to be done by
        // hand too, on top of Window_PreviewMouseDown's own outside-click handling.
        Deactivated += (_, _) => TimesheetSettingsPopup.IsOpen = false;

        // The window is reused (Hide/Show), not recreated, across opens — so it needs its own
        // live-update wiring rather than relying on a fresh instance to pick up new state.
        A.Tick += OnTick;
        A.StateChanged += OnStateChanged;
        IsVisibleChanged += (_, e) => { if (!(bool)e.NewValue) StopBreathing(); };
        InitAi();
    }

    /// <summary>Everything a view switch entails, shared by the toggle's own SelectionChanged
    /// (user click) and SelectView below (programmatic, e.g. driven by the tutorial) — SetIndex
    /// deliberately does NOT raise SelectionChanged on its own (see its remarks), so a
    /// programmatic switch has to run this body itself rather than piggyback on the event.</summary>
    private void ApplyViewModeChange(int idx)
    {
        _viewMode = (ViewMode)idx;
        if (_viewMode == ViewMode.Project)
        {
            _monthStart = StartOfMonth(_weekStart);
            _projectScrollOffset = 0;
        }
        _calendarScrollOffset = null; // re-center on work hours each time Calendar is switched into
        CalendarPxPerHour = 60; // and reset any zoom from a prior calendar session
        RenderCurrentWeek(0, crossFade: true);
        UpdateMergeToggleVisibility(animate: true);
        // Remember this as the view a fresh open should resume on — see ViewMode's remarks.
        A.Settings.DefaultTimesheetView = ModeToSetting(_viewMode);
        A.Store.SaveSettings(A.Settings);
    }

    /// <summary>Programmatically switch view (0 Totals, 1 Calendar, 2 Project) — used by the tutorial to drive
    /// the walkthrough without simulating a click on the toggle itself. A no-op if already on the
    /// requested view.</summary>
    internal void SelectView(int idx)
    {
        if (_viewToggle.SelectedIndex == idx) return;
        _viewToggle.SetIndex(idx, animate: true);
        ApplyViewModeChange(idx);
    }

    // A committed block changed the CSV on disk — the cached week no longer reflects it.
    private void OnStateChanged()
    {
        _cachedWeekStart = null;
        // A drag in progress owns the canvas (and the mouse capture on one of its elements) —
        // rebuilding out from under it would tear down the very Border being dragged. CommitDrag
        // does its own re-render once the drag actually ends, picking this change up then.
        if (!Offscreen && !SuspendRerender) RenderCurrentWeek(0);
    }

    // Once a second: keep the still-running block's segment growing live. Cheap — reuses the
    // cached committed blocks and only recomputes today's aggregation, no disk I/O.
    /// <summary>
    /// Three fix attempts at the once-a-second cursor flicker this replaces (pre-setting the new
    /// canvas's Cursor property; nudging the real OS cursor position; toggling
    /// Mouse.OverrideCursor) all treated the symptom — WPF failing to repaint the cursor
    /// immediately after a full rebuild — without touching the actual cause: a full rebuild
    /// tearing down and recreating every element, including whichever one the mouse happens to be
    /// sitting on, every single second. Nothing forces WPF to correctly and INSTANTLY resolve the
    /// cursor for a brand new element occupying the same screen position an old, now-destroyed
    /// one used to; there's always some gap. The actual fix is to stop rebuilding at all for the
    /// common case: TryUpdateLiveCalendarInPlace mutates the SAME Border/Line elements a full
    /// render already produced (see BuildCalendar's own _liveTick* remarks), so their identity —
    /// and everything hit-testing/hover/cursor resolution hangs off that identity — never changes
    /// in the first place. A full RenderCurrentWeek only remains as the fallback for whatever that
    /// can't handle (switching views/weeks, not currently tracking, day rollover, ...).
    /// </summary>
    private void OnTick()
    {
        if (Offscreen || SuspendRerender) return;

        // Nothing in EITHER view's display changes second-to-second unless today is actually part
        // of the displayed week — the live session (if any) belongs to the current week by
        // definition, and a past/future week's own total is static. Skipping entirely here (not
        // even falling through to a full render) avoids reproducing this whole rework's own
        // flicker bug for someone who happens to be hovering a block in a week that isn't the
        // current one while a tick fires.
        var today = DateTime.Today;
        if (_viewMode == ViewMode.Project)
        {
            if (today < _monthStart || today >= _monthStart.AddMonths(1)) return;
        }
        else if (today < _weekStart || today >= _weekStart.AddDays(7)) return;

        // Idle: nothing in the totals views moves second to second, so a rebuild would only dismiss
        // whatever tooltip is open. Starting/stopping a session re-renders through OnStateChanged.
        if (_viewMode != ViewMode.Calendar && !A.Tracker.IsRunning) return;

        if (TryUpdateLiveBarInPlace(out double barWeekTotal) || TryUpdateLiveProjectInPlace(out barWeekTotal))
        {
            WeekTotalText.Text = $"{barWeekTotal:0.0} h";
            return;
        }

        if (TryUpdateLiveCalendarInPlace(out double weekTotalHours))
        {
            WeekTotalText.Text = $"{weekTotalHours:0.0} h";
            return;
        }
        RenderCurrentWeek(0);
    }

    /// <summary>Attempts the cheap, identity-preserving per-second update described on OnTick.
    /// Returns false (meaning: fall back to a full RenderCurrentWeek) whenever there's nothing
    /// valid to patch in place at all — not showing Calendar view, or neither a live block NOR a
    /// now-line survived the last full render (only possible on the very first tick right after
    /// switching into Calendar view, before that first render has run). Otherwise patches
    /// whichever of the two actually apply: the now-line alone keeps advancing even with nothing
    /// currently tracked; the live block additionally grows only while a session both IS running
    /// and still belongs to the SAME day it was rendered against — a session that stopped, or
    /// rolled past midnight, needs a real rebuild (to remove/reshape it) rather than a patch, so
    /// that block's own case still falls back for those.</summary>
    private bool TryUpdateLiveCalendarInPlace(out double weekTotalHours)
    {
        weekTotalHours = 0;
        if (_viewMode != ViewMode.Calendar) return false;
        if (_liveTickBlockElement is null && _liveTickNowLine is null) return false;

        var now = DateTime.Now;

        if (_liveTickBlockElement is not null)
        {
            if (!A.Tracker.IsRunning || A.Tracker.State.BlockStartUtc is not DateTime startUtc) return false;
            var startLocal = startUtc.ToLocalTime();
            // The block is drawn in one day's column; only the rollover itself needs a real
            // re-layout (to move it to the new day). A session that started on an earlier day is
            // clamped to this column's midnight, same as BuildCalendar — patching it here instead
            // of falling back is what stops an overnight session rebuilding the whole canvas
            // every second for as long as it keeps running.
            if (now.Date != _liveTickDay) return false;

            double startH = startLocal.Date < now.Date ? 0.0 : startLocal.TimeOfDay.TotalHours;
            double endH = now.TimeOfDay.TotalHours;
            double newHeight = Math.Max(3, (endH - startH) * CalendarPxPerHour);

            // A session that was still under one of BuildCalendar's own label-height gates (16px
            // for the code label, 34px for the time range under it) at its very first render never
            // got that label built at all — only ever resizing/repositioning the SAME elements
            // here can't retroactively add one, so once it's grown enough to deserve one, fall
            // back to a real rebuild (which will build it labeled this time) instead of patching
            // it in place, forever unlabeled.
            if (!_liveTickBlockLabeled && newHeight >= 16) return false;
            if (_liveTickBlockLabeled && _liveTickTimeLabel is null && newHeight >= 34) return false;

            Canvas.SetTop(_liveTickBlockElement, startH * CalendarPxPerHour);
            _liveTickBlockElement.Height = newHeight;
            if (_liveTickTimeLabel is not null) _liveTickTimeLabel.Text = $"{startLocal:HH:mm}–{now:HH:mm}";
        }

        if (_liveTickNowLine is not null)
        {
            double nowY = now.TimeOfDay.TotalHours * CalendarPxPerHour;
            _liveTickNowLine.Y1 = nowY;
            _liveTickNowLine.Y2 = nowY;
        }

        // The header's week total still has to tick live regardless of which element(s) above
        // actually got patched — same raw-seconds reasoning HoursOf/RenderCurrentWeek already
        // use, so the number grows smoothly rather than in RenderCurrentWeek's own
        // 0.1h-rounded jumps. Only actually live (not just rendered a moment ago) while IsRunning.
        double weekTotal = GetWeekBlocks().Sum(b => b.DurationHours);
        if (A.Tracker.IsRunning) weekTotal += A.Tracker.CurrentElapsedSeconds / 3600.0;
        weekTotalHours = weekTotal;
        return true;
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

    /// <summary>Open, centred on origin. See the XAML remarks on RootGrid for why this is a plain
    /// content-level fade + scale-settle rather than a "grows out of the button it was opened
    /// from" effect — two attempts at that were each smooth in isolation but broken in
    /// combination with something this window genuinely needs (real native resize/Aero-Snap, and
    /// a MinWidth its header/chart aren't laid out to gracefully shrink past).</summary>
    public void AnimateOpenFrom(Window origin)
    {
        var wa = WorkAreaFor(origin);
        double targetW = Math.Min(1127, wa.Width - 80);
        double targetH = Math.Min(680, wa.Height - 80);
        double targetLeft = Clamp(origin.Left + origin.Width / 2 - targetW / 2, wa.Left + 16, wa.Right - targetW - 16);
        double targetTop = Clamp(origin.Top + origin.Height / 2 - targetH / 2, wa.Top + 16, wa.Bottom - targetH - 16);
        // Bounds are set ONCE, to their final value, never animated — see the XAML remarks.
        Left = targetLeft; Top = targetTop; Width = targetW; Height = targetH;

        // Chart area only — see the XAML remarks on why the header strip is excluded and just
        // appears instantly instead.
        ChartArea.Opacity = 0;
        ScaleXform.ScaleX = 0.96; ScaleXform.ScaleY = 0.96;

        Show();
        Activate();
        // App.OpenTimesheets calls _main.Hide() immediately after this method returns — still
        // within the SAME synchronous call — so this is deferred to run once the dispatcher
        // queue is idle, guaranteeing it's the last word, after that Hide() (and anything else
        // already queued) has fully finished. ForceForeground (not Activate()/Topmost-toggle —
        // both tried, neither reliable) — see its own remarks for why plain SetForegroundWindow
        // calls were being silently ignored even with the deferral.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ForceForeground.Apply(new WindowInteropHelper(this).Handle);
            Focus();
        }), DispatcherPriority.ApplicationIdle);

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ChartArea.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        ScaleXform.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        ScaleXform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });

        _weekStart = StartOfWeek(DateTime.Today);
        _monthStart = StartOfMonth(DateTime.Today);
        _projectScrollOffset = 0;
        // The window is reused across opens, so a stale cache from the last time it was open
        // (possibly still keyed to this same week) must not survive a reopen — otherwise
        // whatever got tracked while it was closed wouldn't show up until the cache happened
        // to be invalidated some other way (navigating weeks, deleting a block).
        _cachedWeekStart = null;
        // Likewise, the view mode resets to the configured default each time the window is
        // (re)opened rather than remembering whatever it was left on — see ViewMode's remarks.
        _viewMode = ModeFromSetting(A.Settings.DefaultTimesheetView);
        _calendarScrollOffset = null;
        CalendarPxPerHour = 60;
        _viewToggle.SetIndex(ModeIndex(_viewMode), animate: false);
        UpdateMergeToggleVisibility(animate: false);
        RenderCurrentWeek(0); // bounds are already final, so this renders at the correct size immediately
    }

    /// <summary>Fade/settle back out, then hide and hand control back.</summary>
    public void AnimateCloseTo(Window target, Action onDone)
    {
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        fadeOut.Completed += (_, _) =>
        {
            // onDone (App.CloseTimesheets) shows the main window BEFORE this one hides — mirrors
            // AnimateOpenFrom, which shows the new window before hiding the old one. Both this
            // window and the main window are ShowInTaskbar="True" (so the app always has a
            // taskbar presence even while main is hidden behind the timesheet view), which means
            // hiding first and showing second — the order this used to run in — left a brief gap
            // with NEITHER window shown, long enough for the taskbar button to visibly disappear
            // and reappear, like the app had relaunched. Overlapping the two removes that gap.
            onDone();
            Hide();
            // Reset so the next AnimateOpenFrom starts clean.
            ChartArea.Opacity = 1;
            ScaleXform.ScaleX = 1; ScaleXform.ScaleY = 1;
        };
        ChartArea.BeginAnimation(OpacityProperty, fadeOut);
        var shrinkEase = new CubicEase { EasingMode = EasingMode.EaseIn };
        ScaleXform.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.96, TimeSpan.FromMilliseconds(180)) { EasingFunction = shrinkEase });
        ScaleXform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.96, TimeSpan.FromMilliseconds(180)) { EasingFunction = shrinkEase });
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
    {
        MaxRestoreBtn.Content = WindowState == WindowState.Maximized ? "❐" : "▢";
        // OnTick skips while minimised, so catch up (and restart the live pulse) on restore.
        if (WindowState == WindowState.Minimized) StopBreathing();
        else if (_wasMinimized && IsVisible && !SuspendRerender) RenderCurrentWeek(0);
        _wasMinimized = WindowState == WindowState.Minimized;
    }

    private bool _wasMinimized;

    // ==================== week navigation ====================

    private void PrevWeek_Click(object sender, RoutedEventArgs e) => Navigate(-1);
    private void NextWeek_Click(object sender, RoutedEventArgs e) => Navigate(1);

    private void Navigate(int weeks)
    {
        if (SuspendRerender) return; // mouse is captured mid-drag (or a delete confirm is showing) — rebuilding now would tear either out from under the user
        if (_viewMode == ViewMode.Project)
        {
            _monthStart = _monthStart.AddMonths(weeks); // one step = one month in Project view
            _projectScrollOffset = 0;
        }
        else
        {
            _weekStart = _weekStart.AddDays(7 * weeks);
        }
        RenderCurrentWeek(weeks);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && TimesheetSettingsPopup.IsOpen) { TimesheetSettingsPopup.IsOpen = false; e.Handled = true; }
        else if (e.Key == Key.Left) { Navigate(-1); e.Handled = true; }
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

    /// <summary>Shift+scroll = "side scroll" week navigation, the same convention used by
    /// browsers/spreadsheets for a vertical wheel doubling as horizontal — works over EITHER
    /// view. Wired as Preview (tunnelling) on the whole chart area, one level above everything
    /// else in it (including the calendar view's own vertically-scrolling ScrollViewer), so it's
    /// guaranteed to see the event and claim it (via ChartArea_MouseWheel's own e.Handled = true)
    /// before that ScrollViewer's default wheel handling ever gets a chance to run — rather than
    /// depending on a handler attached deeper in that view's own tree, nested inside whatever
    /// element happens to be hit.</summary>
    private void ChartArea_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0) return;
        ChartArea_MouseWheel(sender, e); // shares the same debounce/direction logic; sets e.Handled itself
    }

    // NB: a left-edge/left-corner window resize visibly jitters here (right-edge/bottom-only
    // resizing is smooth) — investigated and deliberately left alone for now rather than shipping
    // a half-working mitigation. See notes/resize-jank-investigation.md (gitignored) for the root
    // cause and the two candidate real fixes.
    private void ChartCard_SizeChanged(object sender, SizeChangedEventArgs e) { if (!SuspendRerender) RenderCurrentWeek(0); }

    /// <summary>Ctrl+scroll over the calendar view (see the PreviewMouseWheel hookup in
    /// BuildCalendar) zooms the hour scale in/out, keeping whichever hour is currently centred in
    /// the viewport anchored there — so zooming in/out doesn't also jump you to a different part
    /// of the day.</summary>
    private void ZoomCalendar(int direction, double viewportHeight)
    {
        if (SuspendRerender) return; // don't rebuild the canvas out from under an active drag/popup
        double centerOffset = (_calendarScrollOffset ?? 0) + viewportHeight / 2;
        double centerHour = centerOffset / CalendarPxPerHour;

        double factor = direction > 0 ? 1.15 : 1 / 1.15;
        CalendarPxPerHour = Math.Clamp(CalendarPxPerHour * factor, MinCalendarPxPerHour, MaxCalendarPxPerHour);

        _calendarScrollOffset = centerHour * CalendarPxPerHour - viewportHeight / 2;
        RenderCurrentWeek(0);
    }

    /// <summary>Header "+" button — manual time entry, defaulted to today. Calendar view's
    /// double-click-to-add (see BuildCalendar) is the other entry point into the same dialog.</summary>
    private void AddTime_Click(object sender, RoutedEventArgs e)
    {
        bool added = ShowTimeDialog(() => AddTimeDialog.Ask(this, DateTime.Today));
        if (added) { _cachedWeekStart = null; RenderCurrentWeek(0); }
    }

    // ==================== timesheet settings popover ====================
    // Every timesheet-VIEW setting lives here, and only here — not the main Settings window —
    // so there's exactly one place to look. Every control live-applies (write straight to
    // Settings, save, re-render if it affects the current render) rather than buffering into a
    // working copy for an explicit Save button; the popover has no Save/Cancel of its own.

    // Covers resize/move/drag-to-add alike now — see Settings.DragSnapMinutes.
    private static readonly int[] DragSnapOptions = { 1, 5, 10, 15, 20, 30 };
    private static readonly int[] MinSplitOptions = { 0, 1, 5, 10, 15, 30 };
    // The presets aren't evenly spaced (fine-grained near zero, coarser further out) — see
    // DensitySliderStyle in the XAML, an index-based slider rather than a continuous range.
    private static readonly double[] DensityPresets = { 0, 0.05, 0.1, 0.25, 0.5, 1, 2 };

    private ToggleSwitch _mergeSessionsToggle = null!;

    // Two earlier attempts at "click the gear button while open closes it, doesn't reopen it"
    // both assumed WPF's own StaysOpen="False" light-dismiss and the button's Click could be
    // reasoned about in a fixed order (dismiss-then-Click, so read/snapshot IsOpen before the
    // dismiss / cooldown after it) — in practice that ordering wasn't reliable enough to fix it
    // either way. The Popup is now StaysOpen="True" instead (see its XAML remarks), which removes
    // WPF's own dismiss from the picture entirely: Window_PreviewMouseDown below is the ONLY
    // thing that ever closes it from an outside click, and it explicitly ignores clicks on the
    // gear button itself, so there's no longer two independent pieces of code racing to decide
    // the same click. Click, below, goes back to a plain, un-raced toggle.
    private void TimesheetSettings_Click(object sender, RoutedEventArgs e)
        => TimesheetSettingsPopup.IsOpen = !TimesheetSettingsPopup.IsOpen;

    /// <summary>Closes the popover on a click anywhere outside it — see the XAML remarks on
    /// TimesheetSettingsPopup for why this replaces StaysOpen="False" rather than the popup
    /// handling it natively. Skips two kinds of click: the gear button itself (so THAT click's
    /// own Click event, above, is the only thing that decides what happens to it — otherwise this
    /// would close it out from under the button on mouse-down, and Click would immediately reopen
    /// it on mouse-up, reintroducing the exact race this is meant to avoid) and anything inside
    /// the popover's OWN content (its combo boxes, toggle, slider, ...) — despite living in what
    /// LOOKS like a separate floating surface, a Popup's content is still reachable by the owning
    /// Window's tunnelling Preview events, so without this second check, clicking anything inside
    /// the popover closed it before its own click (e.g. opening a ComboBox's dropdown) ever ran.</summary>
    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!TimesheetSettingsPopup.IsOpen) return;
        if (e.OriginalSource is DependencyObject src)
        {
            if (IsDescendantOf(src, TimesheetSettingsBtn)) return;
            if (TimesheetSettingsPopup.Child is DependencyObject content && IsDescendantOf(src, content)) return;
        }
        TimesheetSettingsPopup.IsOpen = false;
    }

    private static bool IsDescendantOf(DependencyObject element, DependencyObject ancestor)
    {
        for (DependencyObject? cur = element; cur is not null; cur = GetVisualOrLogicalParent(cur))
            if (ReferenceEquals(cur, ancestor)) return true;
        return false;
    }

    /// <summary>Visual-tree parent when there is one, falling back to the LOGICAL parent when the
    /// visual walk dead-ends — which happens at a Popup boundary (e.g. a ComboBox's own dropdown
    /// list is itself a nested Popup, so VisualTreeHelper stops right there). A generated
    /// ComboBoxItem's logical parent is still the ComboBox that produced it, though, so falling
    /// back keeps the walk going back up through it — otherwise clicking an item in one of this
    /// popover's own combo dropdowns read as "outside" everything and closed the popover before
    /// the selection could even land.</summary>
    private static DependencyObject? GetVisualOrLogicalParent(DependencyObject d)
        => (d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : null)
           ?? LogicalTreeHelper.GetParent(d);

    private void InitTimesheetSettingsPopover()
    {
        var s = A.Settings;

        PopulateSnapCombo(DragSnapCombo, DragSnapOptions, s.DragSnapMinutes, v => A.Settings.DragSnapMinutes = v);
        PopulateSnapCombo(MinSplitCombo, MinSplitOptions, s.MinSplitBlockMinutes, v => A.Settings.MinSplitBlockMinutes = v);

        _mergeSessionsToggle = new ToggleSwitch(s.ChartMergeAllSessions,
            tooltip: "Merge every session for a project into one bar.\nOff: only back-to-back sessions with a matching note merge — the same project touched again later gets its own bar.");
        _mergeSessionsToggle.Toggled += on =>
        {
            if (_viewMode == ViewMode.Project) A.Settings.ProjectViewMergeSessions = on;
            else A.Settings.ChartMergeAllSessions = on;
            A.Store.SaveSettings(A.Settings);
            RenderCurrentWeek(0);
        };
        MergeSessionsToggleHost.Content = _mergeSessionsToggle.Root;

        // If the saved value is already index 0, setting Value=0 below is a no-op and
        // ValueChanged never fires — so the label text is applied explicitly here too, not
        // left to only happen as a side effect of the event (same reasoning this had in
        // SettingsWindow before this setting moved here).
        int startIdx = ClosestDensityIndex(s.ChartMinSegmentHours);
        DensitySlider.Value = startIdx;
        RefreshDensityLabel(startIdx);
        DensitySlider.ValueChanged += DensitySlider_ValueChanged;
    }

    /// <summary>Fades the merge-sessions toggle in/out depending on the current view — it only
    /// affects how the bar/Totals view groups a day's sessions, so it's hidden (not just
    /// disabled) whenever Calendar is showing rather than sitting there doing nothing. Opacity-
    /// only (not Collapsed) so the column it lives in doesn't change width mid-fade and shove the
    /// legend's scroll area around; IsHitTestVisible still tracks it so a hidden toggle can't be
    /// clicked through.</summary>
    private void UpdateMergeToggleVisibility(bool animate)
    {
        bool show = _viewMode != ViewMode.Calendar;
        // Totals and Project views each keep their own merge setting, so the one shared switch
        // re-syncs to whichever view is showing.
        bool project = _viewMode == ViewMode.Project;
        _mergeSessionsToggle.SetOn(project ? A.Settings.ProjectViewMergeSessions : A.Settings.ChartMergeAllSessions, animate: false);
        _mergeSessionsToggle.Root.ToolTip = project
            ? "Merge every session for a project into one block.\nOff: each session is its own block."
            : "Merge every session for a project into one bar.\nOff: only back-to-back sessions with a matching note merge — the same project touched again later gets its own bar.";
        MergeSessionsPanel.IsHitTestVisible = show;
        double target = show ? 1 : 0;
        MergeSessionsPanel.BeginAnimation(OpacityProperty, null); // stop any in-flight fade first
        if (animate)
        {
            MergeSessionsPanel.BeginAnimation(OpacityProperty,
                new DoubleAnimation(MergeSessionsPanel.Opacity, target, TimeSpan.FromMilliseconds(160))
                { EasingFunction = new CubicEase { EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseIn } });
        }
        else
        {
            MergeSessionsPanel.Opacity = target;
        }
    }

    /// <summary>Fills a snap/duration dropdown with fixed presets (inserting the current value if
    /// it's somehow outside them, e.g. a hand-edited settings.json) and live-applies on change.</summary>
    private static void PopulateSnapCombo(ComboBox combo, int[] presets, int current, Action<int> apply)
    {
        var items = Array.IndexOf(presets, current) >= 0 ? presets : presets.Append(current).OrderBy(x => x).ToArray();
        combo.ItemsSource = items;
        combo.SelectedItem = current;
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is int v) { apply(v); A.Store.SaveSettings(A.Settings); }
        };
    }

    private static int ClosestDensityIndex(double hours)
    {
        int best = 0;
        double bestDiff = double.MaxValue;
        for (int i = 0; i < DensityPresets.Length; i++)
        {
            double diff = Math.Abs(DensityPresets[i] - hours);
            if (diff < bestDiff) { bestDiff = diff; best = i; }
        }
        return best;
    }

    private void DensitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int idx = (int)Math.Round(e.NewValue);
        A.Settings.ChartMinSegmentHours = DensityPresets[idx];
        A.Store.SaveSettings(A.Settings);
        RefreshDensityLabel(idx);
        RenderCurrentWeek(0); // bar-view-only setting, but harmless (and cheap) to call in calendar view too
    }

    private void RefreshDensityLabel(int idx)
        => DensityValueText.Text = DensityPresets[idx] <= 0 ? "Off" : $"Fold under {DensityPresets[idx]:0.##} h";

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

    private DateTime _cachedRangeEnd;

    // _cachedWeekStart doubles as the cached range's start for the month fetch too — every
    // existing "_cachedWeekStart = null" invalidation therefore covers both views.
    private List<TimeBlock> GetBlocks(DateTime from, DateTime toInclusive)
    {
        if (_cachedWeekStart == from && _cachedRangeEnd == toInclusive && _cachedBlocks is not null) return _cachedBlocks;
        _cachedBlocks = A.Log.ReadRange(from, toInclusive);
        _cachedWeekStart = from;
        _cachedRangeEnd = toInclusive;
        return _cachedBlocks;
    }

    private List<TimeBlock> GetWeekBlocks() => GetBlocks(_weekStart, _weekStart.AddDays(6));

    /// <summary>The displayed week's blocks per day plus the still-running block folded in; shared by the full render and the per-second in-place update.</summary>
    private (Dictionary<DateTime, List<TimeBlock>> ByDay, TimeBlock? Live, double WeekTotal, DateTime Today) BuildWeekData()
    {
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
                ProjectId = active.Id, ProjectCode = active.Code, Asn = active.Asn, ProjectName = active.Name,
                StartLocal = startUtc.ToLocalTime(), EndLocal = DateTime.Now,
                DurationSeconds = A.Tracker.CurrentElapsedSeconds, Notes = "",
            };
            todayBlocks.Add(liveBlock);
            // Raw (unrounded) hours so the live segment grows smoothly every tick, rather than
            // in the 6-minute jumps DurationHours' 0.1h rounding would otherwise produce.
            weekTotal += liveBlock.DurationSeconds / 3600.0;
        }
        return (byDay, liveBlock, weekTotal, today);
    }

    private void RenderCurrentWeek(int slideDirection, bool crossFade = false)
    {
        StopBreathing();
        if (_viewMode == ViewMode.Project)
        {
            RenderCurrentMonth(slideDirection, crossFade);
            return;
        }
        TotalEyebrow.Text = "WEEK TOTAL";
        WeekLabel.Text = FormatWeekLabel(_weekStart);

        var (byDay, liveBlock, weekTotal, today) = BuildWeekData();
        WeekTotalText.Text = $"{weekTotal:0.0} h";

        double w = ChartCard.ActualWidth > 40 ? ChartCard.ActualWidth - 40 : 820;
        double h = ChartCard.ActualHeight > 40 ? ChartCard.ActualHeight - 40 : 440;
        FrameworkElement visual = _viewMode == ViewMode.Calendar
            ? BuildCalendar(byDay, liveBlock, w, h, today)
            : BuildChart(byDay, liveBlock, w, h, today);
        if (crossFade) CrossFade(visual);
        else SlideIn(visual, slideDirection);
        RebuildLegend(byDay.Values.SelectMany(d => d), "week");
    }

    // ==================== project (per-project month) view ====================

    /// <summary>The displayed month's blocks per enabled project plus the still-running block; shared by the full render and the per-second in-place update.</summary>
    private (List<Project> Projects, Dictionary<string, List<TimeBlock>> ByProject, TimeBlock? Live, List<TimeBlock> Shown) BuildMonthData()
    {
        var monthEnd = _monthStart.AddMonths(1).AddDays(-1);
        var projects = A.Tracker.Projects.Where(p => p.Enabled).OrderBy(p => p.Order).ToList();
        var byProject = projects.ToDictionary(p => p.Id, _ => new List<TimeBlock>(), StringComparer.Ordinal);

        var shown = new List<TimeBlock>();
        foreach (var b in GetBlocks(_monthStart, monthEnd))
        {
            var p = A.Tracker.FindByBlock(b);
            if (p is null || !byProject.TryGetValue(p.Id, out var list)) continue;
            list.Add(b);
            shown.Add(b);
        }

        // The still-running block isn't in the CSV yet — fold it in as a synthetic block, as the
        // Totals view does, so it grows live (and is the one block HoursOf measures unrounded).
        TimeBlock? liveBlock = null;
        if (A.Tracker.IsRunning && A.Tracker.Active is { } active && A.Tracker.State.BlockStartUtc is DateTime startUtc
            && byProject.TryGetValue(active.Id, out var activeList)
            && startUtc.ToLocalTime().Date >= _monthStart && startUtc.ToLocalTime().Date <= monthEnd)
        {
            liveBlock = new TimeBlock
            {
                ProjectId = active.Id, ProjectCode = active.Code, Asn = active.Asn, ProjectName = active.Name,
                StartLocal = startUtc.ToLocalTime(), EndLocal = DateTime.Now,
                DurationSeconds = A.Tracker.CurrentElapsedSeconds, Notes = "",
            };
            activeList.Add(liveBlock);
            shown.Add(liveBlock);
        }
        foreach (var list in byProject.Values) list.Sort((a, b) => a.StartLocal.CompareTo(b.StartLocal));

        return (projects, byProject, liveBlock, shown);
    }

    private void RenderCurrentMonth(int slideDirection, bool crossFade)
    {
        TotalEyebrow.Text = "MONTH TOTAL";
        WeekLabel.Text = _monthStart.ToString("MMMM yyyy");

        var (projects, byProject, liveBlock, shown) = BuildMonthData();
        WeekTotalText.Text = $"{shown.Sum(b => HoursOf(b, liveBlock)):0.0} h";

        double w = ChartCard.ActualWidth > 40 ? ChartCard.ActualWidth - 40 : 820;
        double h = ChartCard.ActualHeight > 40 ? ChartCard.ActualHeight - 40 : 440;
        var visual = BuildProjectView(projects, byProject, liveBlock, w, h);
        if (crossFade) CrossFade(visual);
        else SlideIn(visual, slideDirection);
        RebuildLegend(shown, "month");
    }

    private List<ChartSegment> BuildProjectSegments(Project p, List<TimeBlock> blocks, TimeBlock? liveBlock)
    {
        var segs = new List<ChartSegment>();
        var fill = ColorUtil.Brush(p.Color);
        var sessions = blocks.Where(b => HoursOf(b, liveBlock) > 0).ToList();
        if (sessions.Count == 0) return segs;

        if (A.Settings.ProjectViewMergeSessions)
        {
            double total = sessions.Sum(b => HoursOf(b, liveBlock));
            bool live = sessions.Any(b => ReferenceEquals(b, liveBlock));
            string count = $"{sessions.Count} session{(sessions.Count == 1 ? "" : "s")}";
            var lines = sessions.Take(12).Select(b => $"{b.StartLocal:MMM d} · {HoursOf(b, liveBlock):0.0} h");
            string tip = $"{p.Code} · {count} · {total:0.0} h{(live ? " · recording now" : "")}\n" + string.Join("\n", lines);
            if (sessions.Count > 12) tip += $"\n… and {sessions.Count - 12} more";
            segs.Add(new ChartSegment(count, p.Asn, total, fill, tip, sessions[^1].EndLocal, live));
            return segs;
        }

        foreach (var b in sessions)
        {
            double hours = HoursOf(b, liveBlock);
            bool live = ReferenceEquals(b, liveBlock);
            string when = b.Unscheduled
                ? $"{b.StartLocal:ddd MMM d} · by amount"
                : $"{b.StartLocal:ddd MMM d}\n{b.StartLocal:h:mm tt} – {b.EndLocal:h:mm tt}";
            string tip = $"{p.Code} · {hours:0.0} h{(live ? " · recording now" : "")}\n{when}";
            if (!string.IsNullOrWhiteSpace(b.Notes)) tip += $"\nNote: {b.Notes}";
            segs.Add(new ChartSegment($"{b.StartLocal:MMM d}", p.Asn, hours, fill, tip, b.StartLocal, live));
        }
        return segs;
    }

    /// <summary>The recording project's column in the Project view, kept so each tick can update it in place.</summary>
    private sealed record ProjLive(string ProjectId, double AxisMax, double PlotH,
        List<ChartSegment> Segments, List<Border> Borders, TextBlock FooterHours);
    private ProjLive? _projLive;

    /// <summary>Per-second update of the Project view: refits the recording project's column and
    /// the month total. False when more than that would change (axis scale, segment set or label
    /// layout), so the caller falls back to a full render.</summary>
    private bool TryUpdateLiveProjectInPlace(out double monthTotalHours)
    {
        monthTotalHours = 0;
        if (_viewMode != ViewMode.Project || _projLive is not { } live || !A.Tracker.IsRunning) return false;
        var (projects, byProject, liveBlock, shown) = BuildMonthData();
        if (liveBlock is null || liveBlock.ProjectId != live.ProjectId) return false;

        var segsByProject = projects.ToDictionary(pr => pr.Id, pr => BuildProjectSegments(pr, byProject[pr.Id], liveBlock), StringComparer.Ordinal);
        double rawMax = segsByProject.Values.Select(sg => sg.Sum(x => x.Hours)).DefaultIfEmpty(0).Max();
        if (ComputeAxisScale(rawMax).Item1 != live.AxisMax) return false;
        if (!segsByProject.TryGetValue(live.ProjectId, out var segs) || segs.Count != live.Segments.Count) return false;
        for (int i = 0; i < segs.Count; i++)
            if (segs[i].Label != live.Segments[i].Label || segs[i].IsLive != live.Segments[i].IsLive) return false;

        var layout = StackLayout(segs, live.AxisMax, live.PlotH);
        for (int i = 0; i < segs.Count; i++)
            if (live.Borders[i].Tag is not SegRefs refs || refs.Tier != SegmentTier(segs[i], layout.Heights[i])) return false;

        for (int i = 0; i < segs.Count; i++)
        {
            RefitSegment(live.Borders[i], segs[i], layout.Heights[i]);
            Canvas.SetTop(live.Borders[i], layout.Tops[i]);
        }
        live.FooterHours.Text = $"{segs.Sum(x => x.Hours):0.0} h";
        live.Segments.Clear();
        live.Segments.AddRange(segs);
        monthTotalHours = shown.Sum(b => HoursOf(b, liveBlock));
        return true;
    }

    private FrameworkElement BuildProjectView(List<Project> projects, Dictionary<string, List<TimeBlock>> byProject,
        TimeBlock? liveBlock, double width, double height)
    {
        var dimBrush = (Brush)FindResource("TextDim");
        var faintBrush = (Brush)FindResource("TextFaint");

        if (projects.Count == 0)
        {
            return new TextBlock
            {
                Text = "No active projects.", FontSize = 13, Foreground = faintBrush,
                Width = width, Height = height, TextAlignment = TextAlignment.Center, Padding = new Thickness(0, height / 3, 0, 0),
            };
        }

        var gridBrush = (Brush)FindResource("Border");
        var monoFont = (FontFamily)FindResource("MonoFont");

        _projLive = null;
        var segsByProject = projects.ToDictionary(p => p.Id, p => BuildProjectSegments(p, byProject[p.Id], liveBlock), StringComparer.Ordinal);
        double rawMax = segsByProject.Values.Select(s => s.Sum(x => x.Hours)).DefaultIfEmpty(0).Max();
        var (axisMax, step) = ComputeAxisScale(rawMax);

        const double yAxisW = 40;
        const double xAxisH = 40;
        const double minColW = 84;
        const double scrollBarH = 18;
        double viewW = Math.Max(80, width - yAxisW - RightGutter);
        bool needScroll = minColW * projects.Count > viewW;
        double colW = Math.Max(minColW, viewW / projects.Count);
        double plotH = Math.Max(60, height - xAxisH - (needScroll ? scrollBarH : 0));
        var geom = Geom(colW);
        double barW = geom.HoursW;
        double plotW = colW * projects.Count;

        var monthAi = AiOn
            ? AiSessionsBetween(_monthStart, _monthStart.AddMonths(1).AddDays(-1)).Where(s => s.ProjectId is not null)
                .GroupBy(s => s.ProjectId!).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal)
            : new Dictionary<string, List<AiSession>>();
        var costAxis = AiChartMath.NiceAxis(MaxStack(monthAi.Values));

        var axis = new Canvas { Width = yAxisW, Height = plotH + xAxisH };
        var rightAxis = new Canvas { Width = RightGutter, Height = plotH + xAxisH };
        if (AiOn) DrawCostAxis(rightAxis, costAxis, 0, plotH);
        var canvas = new Canvas { Width = plotW, Height = plotH + xAxisH };
        int lines = (int)Math.Round(axisMax / step);
        for (int i = 0; i <= lines; i++)
        {
            double y = plotH - (i * step / axisMax) * plotH;
            canvas.Children.Add(new Line
            {
                X1 = 0, X2 = plotW, Y1 = y, Y2 = y, Stroke = gridBrush, StrokeThickness = 1, SnapsToDevicePixels = true,
            });
            var label = new TextBlock
            {
                Text = $"{i * step:0}h", FontSize = 10.5, Foreground = faintBrush, FontFamily = monoFont,
                Width = yAxisW - 8, TextAlignment = TextAlignment.Right,
            };
            Canvas.SetTop(label, Math.Max(0, y - 7));
            axis.Children.Add(label);
        }

        for (int i = 0; i < projects.Count; i++)
        {
            var p = projects[i];
            var segs = segsByProject[p.Id];
            double colLeft = colW * i;
            double cx = colLeft + geom.HoursCx;
            if (AiOn && monthAi.TryGetValue(p.Id, out var projAi))
                AddAiStack(canvas, projAi, colLeft + geom.AiLeft, geom.AiW, plotH, costAxis.Max);

            double total = segs.Sum(s => s.Hours);
            var layout = StackLayout(segs, axisMax, plotH);
            var segBorders = new List<Border>();
            for (int si = 0; si < segs.Count; si++)
            {
                var block = MakeSegmentBlock(segs[si], barW, layout.Heights[si]);
                Canvas.SetLeft(block, cx - barW / 2);
                Canvas.SetTop(block, layout.Tops[si]);
                canvas.Children.Add(block);
                segBorders.Add(block);
            }
            var footer = new StackPanel { Width = colW, ToolTip = string.IsNullOrWhiteSpace(p.Name) ? p.Code : $"{p.Code} · {p.Name}" };
            footer.Children.Add(new TextBlock
            {
                Text = p.Code, FontSize = 11.5, FontWeight = FontWeights.Medium, Foreground = dimBrush,
                TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 4, 0),
            });
            var footerHours = new TextBlock
            {
                Text = $"{total:0.0} h", FontSize = 9.5, Foreground = faintBrush, TextAlignment = TextAlignment.Center,
            };
            footer.Children.Add(footerHours);
            if (liveBlock is not null && p.Id == liveBlock.ProjectId)
                _projLive = new ProjLive(p.Id, axisMax, plotH, segs, segBorders, footerHours);
            Canvas.SetLeft(footer, colLeft);
            Canvas.SetTop(footer, plotH + 4);
            canvas.Children.Add(footer);
        }

        var sv = new ScrollViewer
        {
            HorizontalScrollBarVisibility = needScroll ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = canvas,
        };
        // The ScrollViewer would otherwise swallow the wheel; here the wheel pans months, like the other views.
        sv.PreviewMouseWheel += (s, e) => ChartArea_MouseWheel(s, e);
        sv.ScrollChanged += (_, e) => { if (e.HorizontalChange != 0) _projectScrollOffset = sv.HorizontalOffset; };
        sv.Loaded += (_, _) => { if (_projectScrollOffset > 0) sv.ScrollToHorizontalOffset(_projectScrollOffset); };

        var root = new Grid { Width = width, Height = height };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(yAxisW) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(RightGutter) });
        root.Children.Add(axis);
        Grid.SetColumn(sv, 1);
        root.Children.Add(sv);
        Grid.SetColumn(rightAxis, 2);
        root.Children.Add(rightAxis);
        return root;
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

    /// <summary>The floor below which the axis never shrinks — a full 8h workday should always
    /// be visible on-scale, even on a week with only short/no entries, rather than making a
    /// couple of hours look misleadingly like "most of the day".</summary>
    private const double AxisFloorHours = 8;

    /// <summary>Rounds up to the nearest multiple of 4 — not 2 — specifically so step
    /// (axisMax / 4) always lands on a whole number. Multiple-of-2 axis maxes (10h, 14h, ...)
    /// produced a 2.5h/3.5h step; the gridline labels round that to a whole number for display
    /// ("0h,00,3h,5h,8h,10h" instead of "0,2.5,5,7.5,10"), which reads as flatly wrong — a
    /// gridline LABELLED "8h" that's actually sitting at 7.5h, right next to the real 8h
    /// reference line and looking like it disagrees with it.</summary>
    private static (double axisMax, double step) ComputeAxisScale(double rawMax)
    {
        double axisMax = Math.Max(AxisFloorHours, Math.Ceiling(rawMax / 4.0) * 4.0);
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
        /// <summary>Resolved live project, via TrackerService.FindByBlock — null if it's since
        /// been deleted (the group is dropped rather than shown, same as before).</summary>
        public required Project? Project;
        /// <summary>Snapshot code from the block(s) that formed this group — only used to build
        /// the identity key below, never for display (Project.Code is used for that so a rename
        /// is reflected immediately, including for blocks recorded under the old code).</summary>
        public required string ProjectCode;
        public double Hours;
        public DateTime Latest = DateTime.MinValue;
        public bool ContainsLive;
        public string? Note;
    }

    /// <summary>Identity key for grouping same-project blocks together even across a rename:
    /// the resolved project's stable Id when it still exists, otherwise its recorded code (so
    /// blocks of a since-deleted project still merge with each other, even though the group is
    /// ultimately dropped for having no live Project to render).</summary>
    private static string GroupKey(Project? resolved, string fallbackCode)
        => resolved?.Id ?? ("code:" + fallbackCode.ToLowerInvariant());

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
    private List<ChartSegment> BuildDaySegments(List<TimeBlock> dayBlocks, Brush otherFill, TimeBlock? liveBlock)
    {
        double minHours = A.Settings.ChartMinSegmentHours;
        var groups = new List<SessionGroup>();

        if (A.Settings.ChartMergeAllSessions)
        {
            var byKey = new Dictionary<string, SessionGroup>(StringComparer.Ordinal);
            foreach (var b in dayBlocks)
            {
                var resolved = A.Tracker.FindByBlock(b);
                string key = GroupKey(resolved, b.ProjectCode);
                if (!byKey.TryGetValue(key, out var g))
                {
                    g = new SessionGroup { Project = resolved, ProjectCode = b.ProjectCode };
                    byKey[key] = g;
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
                var resolved = A.Tracker.FindByBlock(b);
                string key = GroupKey(resolved, b.ProjectCode);
                string note = b.Notes ?? "";
                bool continuesRun = cur is not null
                    && GroupKey(cur.Project, cur.ProjectCode) == key
                    && string.Equals(cur.Note, note, StringComparison.Ordinal);
                if (!continuesRun)
                {
                    cur = new SessionGroup { Project = resolved, ProjectCode = b.ProjectCode, Note = note };
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
            var p = g.Project;
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
                Label: p.Code,
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
                Label: "Other",
                Asn: null,
                Hours: foldedTotal,
                Fill: otherFill,
                Tooltip: $"Other ({folded.Count} entr{(folded.Count == 1 ? "y" : "ies")}) · {foldedTotal:0.0} h\n{breakdown}",
                OrderKey: foldedLatest));
        }

        return kept.OrderBy(s => s.OrderKey).ToList();
    }

    /// <summary>One stacked-bar block — fill, live outline/pulse, and height-gated labels (title
    /// at 20px, ASN at 36px). Shared by the Totals and Project views.</summary>
    /// <summary>Handles on a segment block's labels so a tick can update them without rebuilding.</summary>
    private sealed record SegRefs(TextBlock? Title, TextBlock? Hours, int Tier);

    private static int SegmentTier(ChartSegment s, double segH)
        => (segH >= 20 ? 1 : 0) + (segH >= 34 ? 1 : 0) + (segH >= 50 && !string.IsNullOrEmpty(s.Asn) ? 1 : 0);

    /// <summary>Re-fits an existing segment block to new data; the caller has already checked the
    /// label layout (tier) is unchanged.</summary>
    private static void RefitSegment(Border seg, ChartSegment s, double segH)
    {
        var refs = (SegRefs)seg.Tag;
        seg.Height = segH;
        seg.ToolTip = s.Tooltip;
        string hoursText = $"{s.Hours:0.#}h";
        if (refs.Hours is not null) refs.Hours.Text = hoursText;
        else if (refs.Title is not null) refs.Title.Text = $"{s.Label} · {hoursText}";
    }

    private Border MakeSegmentBlock(ChartSegment seg0, double barW, double segH)
    {
        var baseColor = ((SolidColorBrush)seg0.Fill).Color;

        var seg = new Border
        {
            Width = barW, Height = segH,
            Background = seg0.Fill,
            CornerRadius = new CornerRadius(4),
            ToolTip = seg0.Tooltip,
            ClipToBounds = true,
        };
        TextBlock? titleRef = null, hoursRef = null;
        // A border (not text) is the primary "live" cue since it reads even on a 2px sliver; it's
        // a brighter tint of the segment's own colour rather than the unrelated red "Live" accent.
        if (seg0.IsLive)
        {
            var liveBorder = new SolidColorBrush(Lighten(baseColor, 0.4));
            seg.BorderBrush = liveBorder;
            seg.BorderThickness = new Thickness(2);
            Breathe(liveBorder, SolidColorBrush.OpacityProperty, 0.55, 1.0);
        }

        var content = new Grid();
        if (segH >= 20)
        {
            var ink = new SolidColorBrush(SystemAccent.ReadableInk(baseColor));
            var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6, 4, 6, 2) };

            // Hours sit on their own line under the title when there's room; in a short box they
            // stay inline so the count is never dropped.
            string hoursText = $"{seg0.Hours:0.#}h";
            bool hoursOwnLine = segH >= 34;
            var title = new TextBlock
            {
                Text = hoursOwnLine ? seg0.Label : $"{seg0.Label} · {hoursText}",
                FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = ink,
                TextWrapping = TextWrapping.Wrap,
            };
            titleRef = title;
            if (seg0.IsLive)
            {
                // Grid (not a horizontal StackPanel) so a long title wraps instead of pushing the dot out.
                var titleRow = new Grid();
                titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                titleRow.Children.Add(title);
                var dot = new TextBlock { Text = "  ●", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = ink };
                // Phase-locked (see BreathingAnimation) because the live segment is rebuilt every second.
                Breathe(dot, OpacityProperty, 1.0, 0.3);
                Grid.SetColumn(dot, 1);
                titleRow.Children.Add(dot);
                labels.Children.Add(titleRow);
            }
            else
            {
                labels.Children.Add(title);
            }

            if (hoursOwnLine)
            {
                hoursRef = new TextBlock
                {
                    Text = hoursText, FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = ink, Opacity = 0.9,
                    Margin = new Thickness(0, 1, 0, 0),
                };
                labels.Children.Add(hoursRef);
            }

            if (segH >= 50 && !string.IsNullOrEmpty(seg0.Asn))
            {
                labels.Children.Add(new TextBlock
                {
                    Text = $"ASN {seg0.Asn}", FontSize = 9, Foreground = ink, Opacity = 0.85,
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0),
                });
            }
            content.Children.Add(labels);
        }

        seg.Child = content;
        seg.Tag = new SegRefs(titleRef, hoursRef, SegmentTier(seg0, segH));
        return seg;
    }

    // Segments stack bottom-up with a small gap; the day's overall stack (bars + gaps) must land
    // exactly where its total hours says it should on the axis, so the gaps are carved OUT of the
    // segments' own share of the height rather than added on top (otherwise a day chopped into many
    // segments would reach further up the scale than an equal-hours day with one or two).
    private static (double[] Tops, double[] Heights, double StackTop) StackLayout(
        IReadOnlyList<ChartSegment> segments, double axisMax, double plotH)
    {
        const double segGap = 2;
        double dayTotalHours = segments.Sum(s => s.Hours);
        double idealStackH = dayTotalHours / axisMax * plotH;
        double gapTotal = Math.Max(0, segments.Count - 1) * segGap;
        double availableForBars = Math.Max(0, idealStackH - gapTotal);
        var tops = new double[segments.Count];
        var heights = new double[segments.Count];
        double yCursor = plotH;
        for (int i = 0; i < segments.Count; i++)
        {
            double segH = dayTotalHours > 0 ? Math.Max(2, segments[i].Hours / dayTotalHours * availableForBars) : 2;
            heights[i] = segH;
            tops[i] = yCursor - segH;
            yCursor -= segH + segGap;
        }
        return (tops, heights, yCursor);
    }

    /// <summary>Today's column in the totals bar view, kept so each tick can update it in place.</summary>
    private sealed record BarLive(Canvas Canvas, DateTime Day, double AxisMax, double PlotH,
        List<ChartSegment> Segments, List<Border> Borders, Border Plus);
    private BarLive? _barLive;

    /// <summary>Per-second update of the bar view: refits only today's segments and the plus button
    /// above them. False when anything beyond that would change (axis scale, segment set or label
    /// layout), so the caller falls back to a full render.</summary>
    private bool TryUpdateLiveBarInPlace(out double weekTotalHours)
    {
        weekTotalHours = 0;
        if (_viewMode != ViewMode.Bar || _barLive is not { } live || !A.Tracker.IsRunning) return false;
        var (byDay, liveBlock, weekTotal, today) = BuildWeekData();
        if (today != live.Day || liveBlock is null || !byDay.TryGetValue(today, out var todayBlocks)) return false;

        double rawMax = byDay.Values.Select(day => day.Sum(b => HoursOf(b, liveBlock))).DefaultIfEmpty(0).Max();
        if (ComputeAxisScale(rawMax).Item1 != live.AxisMax) return false;

        var segs = BuildDaySegments(todayBlocks, otherFill: (Brush)FindResource("TextFaint"), liveBlock: liveBlock);
        if (segs.Count != live.Segments.Count) return false;
        for (int i = 0; i < segs.Count; i++)
            if (segs[i].Label != live.Segments[i].Label || segs[i].IsLive != live.Segments[i].IsLive) return false;

        var layout = StackLayout(segs, live.AxisMax, live.PlotH);
        for (int i = 0; i < segs.Count; i++)
            if (live.Borders[i].Tag is not SegRefs refs || refs.Tier != SegmentTier(segs[i], layout.Heights[i])) return false;

        for (int i = 0; i < segs.Count; i++)
        {
            RefitSegment(live.Borders[i], segs[i], layout.Heights[i]);
            Canvas.SetTop(live.Borders[i], layout.Tops[i]);
        }
        Canvas.SetTop(live.Plus, Math.Max(0, layout.StackTop - 28));
        live.Segments.Clear();
        live.Segments.AddRange(segs);
        weekTotalHours = weekTotal;
        return true;
    }

    private FrameworkElement BuildChart(Dictionary<DateTime, List<TimeBlock>> byDay, TimeBlock? liveBlock,
        double width, double height, DateTime today)
    {
        double rawMax = byDay.Values.Select(day => day.Sum(b => HoursOf(b, liveBlock))).DefaultIfEmpty(0).Max();
        var (axisMax, step) = ComputeAxisScale(rawMax);

        const double yAxisW = 40;
        const double xAxisH = 32;
        double plotW = Math.Max(80, width - yAxisW - RightGutter);
        double plotH = Math.Max(60, height - xAxisH);

        var canvas = new Canvas { Width = width, Height = height };
        _barLive = null;
        var weekAi = AiOn ? AiSessionsBetween(_weekStart, _weekStart.AddDays(6)) : new List<AiSession>();
        var aiByDay = weekAi.GroupBy(AiChartMath.LocalDay).ToDictionary(g => g.Key, g => g.ToList());
        var costAxis = AiChartMath.NiceAxis(MaxStack(aiByDay.Values));
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
                X1 = yAxisW, X2 = yAxisW + plotW, Y1 = y, Y2 = y,
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

        // A dedicated "8 hour day" marker, separate from the regular gridlines above — those
        // land at even fractions of whatever the axis max happens to be, which only coincides
        // with 8h when the axis is sitting right at its floor. Once a day pushes the axis taller
        // than 8h, this is the only line still marking a full workday. Dashed and drawn on top of
        // (but more subtly than) the solid gridlines so it reads as a deliberate target, not just
        // another tick — skipped when it would exactly double up on the top gridline.
        if (axisMax > AxisFloorHours)
        {
            double y8 = plotH - (AxisFloorHours / axisMax) * plotH;
            canvas.Children.Add(new Line
            {
                X1 = yAxisW, X2 = yAxisW + plotW, Y1 = y8, Y2 = y8,
                Stroke = accentBrush, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 },
                Opacity = 0.55, SnapsToDevicePixels = true,
            });
        }

        var days = byDay.Keys.OrderBy(d => d).ToList();
        double dayW = plotW / 7.0;
        var geom = Geom(dayW);
        double barW = geom.HoursW;
        if (AiOn) DrawCostAxis(canvas, costAxis, yAxisW + plotW, plotH);

        for (int i = 0; i < days.Count; i++)
        {
            var day = days[i];
            double colLeft = yAxisW + dayW * i;
            double cx = colLeft + geom.HoursCx;
            if (AiOn && aiByDay.TryGetValue(day, out var daySessions))
                AddAiStack(canvas, daySessions, colLeft + geom.AiLeft, geom.AiW, plotH, costAxis.Max);

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
            var segments = BuildDaySegments(byDay[day], otherFill: faintBrush,
                liveBlock: day == today ? liveBlock : null);

            var layout = StackLayout(segments, axisMax, plotH);
            var segBorders = new List<Border>();
            for (int si = 0; si < segments.Count; si++)
            {
                var seg = MakeSegmentBlock(segments[si], barW, layout.Heights[si]);
                Canvas.SetLeft(seg, cx - barW / 2);
                Canvas.SetTop(seg, layout.Tops[si]);
                canvas.Children.Add(seg);
                segBorders.Add(seg);
            }
            double yCursor = layout.StackTop;

            // A small "+" riding just above this day's bar — same Add dialog as the header's own
            // "+" (Amount mode), just pre-set to this column's day instead of always today, the
            // same way double-clicking blank space in Calendar view pre-sets the day it was
            // clicked on. Positioned off yCursor, which this render just finished stacking down
            // from plotH to the bar's current top — so on a live day it rides up with the bar as
            // the running segment grows, rather than sitting at a fixed height.
            var plusBtn = new Border
            {
                Width = 20, Height = 20, CornerRadius = new CornerRadius(10),
                Background = (Brush)FindResource("Surface3"), Cursor = Cursors.Hand,
                ToolTip = "Add time for this day",
                Child = new TextBlock
                {
                    Text = "＋", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = dimBrush,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            Canvas.SetLeft(plusBtn, cx - 10);
            Canvas.SetTop(plusBtn, Math.Max(0, yCursor - 28));
            var dayForAdd = day;
            plusBtn.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                bool added = ShowTimeDialog(() => AddTimeDialog.Ask(this, dayForAdd));
                if (added) { _cachedWeekStart = null; RenderCurrentWeek(0); }
            };
            canvas.Children.Add(plusBtn);
            if (day == today)
                _barLive = new BarLive(canvas, day, axisMax, plotH, segments, segBorders, plusBtn);

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

    // ==================== calendar (24h time-of-day) view ====================
    // A second renderer, parallel to BuildChart above rather than a variant of it — the bar view
    // stacks each day's blocks into per-project TOTALS; the calendar view places every block at
    // its own actual time of day, unstacked, so a day's ChartMergeAllSessions/ChartMinSegmentHours
    // display-only grouping (which exists purely to make a bar chart legible) doesn't apply here
    // at all. It reuses BuildChart's yAxisW/xAxisH/dayW/barW geometry so the 7 day columns stay
    // pixel-aligned between the two views — both for visual consistency and because that
    // alignment is what a future block-morph transition between them would ride on.

    /// <summary>Vertical pixels per hour of the 24h grid — a 30-minute block is 30px tall.
    /// Mutable (not a const) — Ctrl+scroll zooms this in/out, see ChartArea_MouseWheel/ZoomCalendar.</summary>
    private double CalendarPxPerHour = 60;
    private const double MinCalendarPxPerHour = 24;
    private const double MaxCalendarPxPerHour = 160;

    // ==================== once-a-second live update, without a full rebuild ====================
    // References into the LAST full BuildCalendar render's own live-block Border/label and "now"
    // line — captured there (and reset to null at the top of every BuildCalendar call, so a stale
    // reference never survives past the render it came from) purely so OnTick can mutate their
    // Height/Top/Text/Y in place instead of tearing down and rebuilding the whole canvas every
    // second. That full rebuild used to be the ONLY way the live block grew, but replacing every
    // element every tick — including the one the mouse happens to be sitting on — meant the
    // cursor (and hover-fade state) briefly had nothing to resolve against each time, reading as
    // a flicker in lock-step with the tick. Mutating the SAME elements in place never changes
    // their identity, so hit-testing/hover/cursor are never disturbed at all. See
    // TryUpdateLiveCalendarInPlace, and OnTick, which prefers it over RenderCurrentWeek whenever
    // there's something valid to patch.
    private Border? _liveTickBlockElement;
    private TextBlock? _liveTickTimeLabel;
    private Line? _liveTickNowLine;
    /// <summary>Whether the last full render actually gave the live block its code/time label —
    /// false for a session so freshly started that its very first render landed below the 16px
    /// MinHeightForGrips threshold (see BuildCalendar's own labelling gate). The per-second patch
    /// below only ever resizes/repositions the SAME elements; it never adds a label that wasn't
    /// there to begin with, so a session that started sub-threshold would otherwise keep growing
    /// tick after tick with no title ever appearing. See TryUpdateLiveCalendarInPlace.</summary>
    private bool _liveTickBlockLabeled;
    /// <summary>The day column the live block was rendered into — see TryUpdateLiveCalendarInPlace.</summary>
    private DateTime _liveTickDay;

    private FrameworkElement BuildCalendar(Dictionary<DateTime, List<TimeBlock>> byDay, TimeBlock? liveBlock,
        double width, double height, DateTime today)
    {
        // Reset here, unconditionally — repopulated below only if THIS render actually produces a
        // live block/now-line; a stale reference from a previous render (e.g. after navigating
        // away from today's week, or the session stopping) must never survive to be mutated by a
        // later OnTick as if it were still valid.
        _liveTickBlockElement = null;
        _liveTickTimeLabel = null;
        _liveTickNowLine = null;
        _liveTickBlockLabeled = false;

        const double yAxisW = 40;
        const double xAxisH = 32; // matches BuildChart's day-label strip height
        double plotW = Math.Max(80, width - yAxisW - RightGutter);
        double gridHeight = 24 * CalendarPxPerHour;

        // Unscheduled ("by amount") entries render as small chips in the day header rather than
        // in the timed grid (they have no clock position — see TimeBlock.Unscheduled). Reserving
        // their height needs to happen before bodyHeight/headerCanvas below are sized, so it's
        // computed straight off byDay rather than discovered mid-render — capped at a couple of
        // rows so one day with a pile of them doesn't blow out every column's header height; any
        // more collapse into a single "+N more" row.
        const double chipRowH = 18;
        const int maxChipRows = 2;
        int maxUnscheduled = byDay.Values.Select(list => list.Count(b => b.Unscheduled)).DefaultIfEmpty(0).Max();
        double chipAreaH = Math.Min(maxUnscheduled, maxChipRows) * chipRowH;
        double headerH = xAxisH + chipAreaH;

        // The day-header row is a FIXED strip, pinned at the bottom of the view (see the `root`
        // Grid at the end of this method), not part of the scrolling canvas — so the scrollable
        // body only needs to fit the grid itself, and the ScrollViewer only gets what's left
        // after that fixed strip.
        double bodyHeight = Math.Max(40, height - headerH);

        var canvas = new Canvas { Width = width, Height = gridHeight };

        // While a live block is ticking, the WHOLE canvas rebuilds every second (see OnTick) to
        // grow it — a fresh grip element appearing under an otherwise-STATIONARY cursor still
        // fires a genuine MouseEnter (WPF re-synchronises hit-testing after every layout change,
        // and this is a brand new element as far as it's concerned), animating in from scratch
        // each time: a visible flicker in lock-step with the live pulse. Capturing the cursor's
        // position against the OUTGOING canvas (still in the visual tree at this point — it's
        // only swapped out once this method returns, via SlideIn/CrossFade) lets each block below
        // know synchronously, before its grips even exist, whether it should render already-
        // hovered — sidestepping any ordering question between this render's own Loaded and
        // MouseEnter notifications entirely.
        Point? mouseInOldCanvas = null;
        // The previous render is now a Grid (fixed header strip + ScrollViewer), not a bare
        // ScrollViewer — see the `root` Grid built at the bottom of this method. Searched by type
        // rather than assumed at a fixed child index, since which row the header/scroll body land
        // in (and so which index each is added at) is a display choice, not a structural constant.
        if (_currentChart is Grid prevGrid)
        {
            foreach (var child in prevGrid.Children)
            {
                if (child is not ScrollViewer { Content: Canvas oldCanvas } || !oldCanvas.IsLoaded) continue;
                // This is purely a cosmetic optimisation (avoiding a redundant fade-in) — if the
                // visual is mid-teardown or otherwise not positioned yet, just skip it rather than
                // risk a crash over it.
                try { mouseInOldCanvas = Mouse.GetPosition(oldCanvas); } catch (InvalidOperationException) { }
                break;
            }
        }

        // Same rationale as mouseInOldCanvas's own remarks just above (and reusing its result) —
        // the drag-to-add hover cursor (see canvas.MouseMove further down) is only (re)applied by
        // an event handler that fires on an actual mouse MOVE, so without this, the once-a-second
        // live-tick rebuild — which replaces this Canvas outright — visibly flashed the cursor
        // back to the default arrow every second, in lockstep with the live pulse, until the
        // mouse so much as twitched enough to re-trigger MouseMove. Blocks/grips still win the
        // hit-test the moment they exist below, exactly like that live handler already relies on.
        canvas.Cursor = mouseInOldCanvas is { } mpForCursor && mpForCursor.X >= yAxisW ? CustomCursors.SmallPlus : Cursors.Arrow;

        // Fixed day-of-week/date strip, pinned at the bottom of the view instead of living at the
        // bottom of the SCROLLING hour grid (gridHeight is typically ~1000px+ — that meant
        // scrolling all the way down just to see which column is which day). Populated per-day
        // inside the main loop below, alongside everything else that needs that same per-day
        // geometry.
        var headerCanvas = new Canvas { Width = width, Height = headerH, Background = Brushes.Transparent };

        var gridBrush = (Brush)FindResource("Border");
        var faintBrush = (Brush)FindResource("TextFaint");
        var dimBrush = (Brush)FindResource("TextDim");
        var accentBrush = (Brush)FindResource("Accent");
        var monoFont = (FontFamily)FindResource("MonoFont");

        double dayW = plotW / 7.0;
        var geom = Geom(dayW); // same column math as BuildChart, for alignment
        double barW = geom.HoursW;
        var aiByDay = (AiOn ? AiSessionsBetween(byDay.Keys.Min(), byDay.Keys.Max()) : new List<AiSession>())
            .GroupBy(AiChartMath.LocalDay).ToDictionary(g => g.Key, g => g.ToList());

        // Hour gridlines + labels, one per hour — there's room for all 24 (unlike the bar view's
        // 4-5 fractional steps), which also makes each one land on a whole, readable hour.
        for (int hour = 0; hour <= 24; hour++)
        {
            double y = hour * CalendarPxPerHour;
            canvas.Children.Add(new Line
            {
                X1 = yAxisW, X2 = width, Y1 = y, Y2 = y,
                Stroke = gridBrush, StrokeThickness = 1, SnapsToDevicePixels = true,
                Opacity = hour % 6 == 0 ? 1.0 : 0.55, // the 6-hour marks (12/6/12/6) read a touch stronger
            });
            if (hour == 24) continue; // no trailing "12 AM" label past the last line
            var label = new TextBlock
            {
                Text = new DateTime(1, 1, 1, hour, 0, 0).ToString("h tt"),
                FontSize = 10.5, Foreground = faintBrush, FontFamily = monoFont,
                Width = yAxisW - 8, TextAlignment = TextAlignment.Right,
            };
            Canvas.SetLeft(label, 0);
            Canvas.SetTop(label, y + 3);
            canvas.Children.Add(label);
        }

        // Tracks each rendered block's bounds so the double-click-to-add handler below can tell
        // "blank space" apart from "on top of an existing block" — double-clicking a real block
        // isn't the empty-space-add gesture (Phase 4 gives blocks their own drag interaction).
        var blockRects = new List<Rect>();

        // ==================== delete confirmation popup ====================
        // A small floating card, positioned next to whichever block's "✕" was clicked — the same
        // confirm-before-delete idea as DayBlocksWindow's inline row swap, but blocks here are
        // small absolutely-positioned canvas elements rather than list rows with room to spare,
        // so the confirmation is a separate overlay instead of replacing the block's own content.
        // `openConfirm` is a plain local (not a field) — closing over it from BOTH the block loop
        // below and CloseConfirm/ShowDeleteConfirm is enough, since a single method call always
        // builds and owns exactly one canvas. _confirmingDelete (a field) is the piece that
        // actually needs to survive between renders — see its remarks.
        Border? openConfirm = null;

        void CloseConfirm()
        {
            if (openConfirm is null) return;
            canvas.Children.Remove(openConfirm);
            openConfirm = null;
            _confirmingDelete = false;
        }

        void ShowDeleteConfirm(TimeBlock target, double blockLeft, double blockTop, double blockWidth, double blockHeight)
        {
            CloseConfirm();
            const double cardWidth = 172;

            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = "Delete this block?", FontSize = 12, FontWeight = FontWeights.Medium,
                Foreground = (Brush)FindResource("Live"), Margin = new Thickness(0, 0, 0, 8),
            });
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancelBtn = new Button
            {
                Content = "Cancel", Style = (Style)FindResource("Btn"), FontSize = 11,
                Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(0, 0, 6, 0),
            };
            var deleteBtn = new Button
            {
                Content = "Delete", Style = (Style)FindResource("BtnAccent"), FontSize = 11,
                Padding = new Thickness(9, 4, 9, 4), Background = (Brush)FindResource("Live"),
            };
            row.Children.Add(cancelBtn);
            row.Children.Add(deleteBtn);
            stack.Children.Add(row);

            var card = new Border
            {
                Width = cardWidth, Background = (Brush)FindResource("Surface2"), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8, 10, 8), Child = stack, Opacity = 0,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                { BlurRadius = 8, ShadowDepth = 1, Opacity = 0.3, Color = (Color)ColorConverter.ConvertFromString("#161C2A") },
            };

            double left = Clamp(blockLeft + blockWidth / 2 - cardWidth / 2, yAxisW + 2, Math.Max(yAxisW + 2, width - cardWidth - 2));
            const double approxCardHeight = 64;
            bool roomAbove = blockTop >= approxCardHeight + 6;
            double top = roomAbove ? blockTop - approxCardHeight - 6 : blockTop + blockHeight + 6;
            Canvas.SetLeft(card, left);
            Canvas.SetTop(card, top);
            Canvas.SetZIndex(card, 1000);
            canvas.Children.Add(card);
            card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
            openConfirm = card;
            _confirmingDelete = true;

            cancelBtn.Click += (_, e) => { e.Handled = true; CloseConfirm(); };
            deleteBtn.Click += (_, e) =>
            {
                e.Handled = true;
                A.Log.DeleteBlock(target);
                CloseConfirm();
                _cachedWeekStart = null;
                RenderCurrentWeek(0);
            };
        }

        var days = byDay.Keys.OrderBy(d => d).ToList();
        // Every rendered piece of each stored row, so a drag can keep a midnight-crossing row's
        // pieces on different days in step with each other while it's in flight.
        var rowElements = new Dictionary<string, List<(DateTime Day, Border El)>>();
        for (int i = 0; i < days.Count; i++)
        {
            var day = days[i];
            double colLeft = yAxisW + dayW * i;
            double cx = colLeft + geom.HoursCx;

            if (day == today)
            {
                var ac = ((SolidColorBrush)accentBrush).Color;
                var hl = new Rectangle
                {
                    Width = dayW - 4, Height = gridHeight, RadiusX = 6, RadiusY = 6,
                    Fill = new SolidColorBrush(Color.FromArgb(20, ac.R, ac.G, ac.B)),
                };
                Canvas.SetLeft(hl, colLeft + 2);
                Canvas.SetTop(hl, 0);
                canvas.Children.Add(hl);
            }

            // ALL of the day's rendered blocks (draggable or not — including the live block and
            // the live block), so neighbour/
            // collision lookups always see the true immediate neighbour even when it happens to
            // be one of those non-draggable ones. draggableDayBlockElements is the subset that
            // actually gets a move handler (and, per HasGrips, resize grips too) wired up in the
            // second pass further down.
            var allDayBlockElements = new List<(TimeBlock Block, Border Element)>();
            var draggableDayBlockElements = new List<(TimeBlock Block, Border Element, Border? TopGrip, Border? BotGrip)>();

            foreach (var b in byDay[day])
            {
                // Unscheduled ("by amount") entries have no real clock position — see
                // TimeBlock.Unscheduled — so they don't belong in the timed grid at all; they're
                // rendered as chips in the pinned day header instead, below.
                if (b.Unscheduled) continue;
                bool isLive = ReferenceEquals(b, liveBlock);
                // A block that runs past this day's midnight has its Border's height clamped
                // (below) to the visible 24:00 boundary purely for display — if it were dragged,
                // committing would treat that TRUNCATED height as the real one and silently chop
                // off whatever ran past midnight. Simplest safe answer: such blocks (rare — one
                // continuous session spanning midnight without a stop/switch) just aren't
                // draggable; isLive's exclusion below covers the mirror case (starts before
                // today, only possible for the synthetic live block).
                // Clamp to this column's own midnight-to-midnight span — a block that started
                // before this day (only possible for the synthetic live block, force-bucketed
                // under "today" even if it actually started yesterday) or that runs past this
                // day's midnight renders truncated at the boundary rather than at a negative or
                // wildly out-of-range offset.
                double startH = b.StartLocal.Date < day ? 0.0 : b.StartLocal.TimeOfDay.TotalHours;
                double endH = b.EndLocal.Date > day ? 24.0 : b.EndLocal.TimeOfDay.TotalHours;
                double top = startH * CalendarPxPerHour;
                double segH = Math.Max(3, (endH - startH) * CalendarPxPerHour);

                var resolved = A.Tracker.FindByBlock(b);
                var baseColor = resolved is not null ? ColorUtil.Parse(resolved.Color) : ((SolidColorBrush)faintBrush).Color;
                var fill = new SolidColorBrush(baseColor);

                // A slice of a midnight-crossing row shows the whole row's span.
                var whole = b.Whole ?? b;
                string span = whole.EndLocal.Date > whole.StartLocal.Date
                    ? $"{whole.StartLocal:MMM d HH:mm}–{whole.EndLocal:MMM d HH:mm}"
                    : $"{b.StartLocal:HH:mm}–{b.EndLocal:HH:mm}";
                string tooltip = resolved is not null
                    ? $"{resolved.Code} · ASN {resolved.Asn} · {span}" + (isLive ? " · recording now" : "")
                    : $"{b.ProjectCode} · {span}";
                if (!string.IsNullOrWhiteSpace(b.Notes)) tooltip += $"\nNote: {b.Notes}";

                var block = new Border
                {
                    Width = barW, Height = segH, Background = fill,
                    CornerRadius = new CornerRadius(4), ToolTip = tooltip, ClipToBounds = true,
                };

                if (isLive)
                {
                    var liveBorder = new SolidColorBrush(Lighten(baseColor, 0.4));
                    block.BorderBrush = liveBorder;
                    block.BorderThickness = new Thickness(2);
                    Breathe(liveBorder, SolidColorBrush.OpacityProperty, 0.55, 1.0);
                }

                // The block's content is always a Grid (even when there's nothing to label) so
                // the drag-handle grips below can be added as overlay children regardless of
                // whether there's room for text — a 5-minute sliver is still draggable, just
                // unlabelled.
                var content = new Grid();
                // Captured (when isLive) so OnTick's per-second in-place update can rewrite this
                // text directly instead of triggering a full rebuild — see _liveTickTimeLabel.
                TextBlock? timeLabel = null;
                if (segH >= 16 && resolved is not null)
                {
                    var ink = new SolidColorBrush(SystemAccent.ReadableInk(baseColor));
                    var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(5, 3, 5, 2) };
                    labels.Children.Add(new TextBlock
                    {
                        Text = isLive ? resolved.Code + "  ●" : resolved.Code, FontSize = 10.5,
                        FontWeight = FontWeights.SemiBold, Foreground = ink, TextWrapping = TextWrapping.Wrap,
                    });
                    if (segH >= 34)
                    {
                        timeLabel = new TextBlock
                        {
                            Text = $"{b.StartLocal:HH:mm}–{b.EndLocal:HH:mm}", FontSize = 9, Foreground = ink, Opacity = 0.85,
                            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0),
                        };
                        labels.Children.Add(timeLabel);
                    }
                    // The note, once there's room for a third line — faded to 50% (on top of the
                    // time label's own 0.85, so it reads as clearly secondary to it) rather than
                    // full-strength, since it's supplementary detail, not the primary label.
                    if (segH >= 50 && !string.IsNullOrWhiteSpace(b.Notes))
                    {
                        labels.Children.Add(new TextBlock
                        {
                            Text = b.Notes, FontSize = 9, Foreground = ink, Opacity = 0.5,
                            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0),
                        });
                    }
                    content.Children.Add(labels);
                }

                // A fixed grip/✕ size would swallow most or all of a short block's clickable
                // area (an 8-18px affordance on a 15-20px/15-20min block leaves no room to grab
                // the body for a move, or even fit, at all) — both disappear entirely below this
                // height, leaving the whole block movable (but not resizable/deletable from the
                // block itself) instead of forcing an accidental resize or an overflowing ✕. Such
                // a block is still reachable via click-to-edit below (unconditional on height) —
                // its dialog has its own Delete link, and typed Start/End covers resize.
                const double MinHeightForGrips = 16;

                // Small "✕" delete affordance, top-right corner, hidden until the block is
                // hovered — same fade treatment as the drag grips below. Not offered on the live
                // block: it isn't a recorded row yet, so there's nothing to delete (stop/switch
                // it from the main window instead).
                Border? deleteBtn = null;
                if (!isLive && segH >= MinHeightForGrips)
                {
                    // Transparent hit-area (a bit bigger than the glyph itself, for an easier
                    // click target) with no visible fill — just the faint ✕ on top, no chip/circle
                    // behind it. A subtle drop shadow keeps it legible over light block colours
                    // without needing an opaque backing.
                    deleteBtn = new Border
                    {
                        Width = 18, Height = 18, Background = Brushes.Transparent,
                        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(0, 2, 2, 0), Cursor = Cursors.Hand, Opacity = 0,
                        Child = new TextBlock
                        {
                            Text = "✕", FontSize = 9.5, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
                            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                            Effect = new System.Windows.Media.Effects.DropShadowEffect
                            { BlurRadius = 3, ShadowDepth = 0, Opacity = 0.7, Color = Colors.Black },
                        },
                    };
                    content.Children.Add(deleteBtn);
                }

                bool draggable = !isLive;
                bool showGrips = draggable && segH >= MinHeightForGrips;
                Border? topGrip = null, botGrip = null;
                // Non-draggable blocks (live, or the rare one spanning past midnight) still get
                // their own explicit Arrow — without it, hovering one would fall through to the
                // canvas's own crosshair (see the drag-to-add hover cursor further down),
                // misleadingly suggesting you could add right where something already is.
                block.Cursor = draggable ? Cursors.SizeAll : Cursors.Arrow;

                // See mouseInOldCanvas above — if the cursor's already resting where this block
                // is about to land, show its hover affordances (grips, delete ✕) immediately (no
                // fade) instead of starting hidden and animating in, which is what produced the
                // once-a-second flicker while a live block ticks.
                var blockRect = new Rect(cx - barW / 2, top, barW, segH);
                bool alreadyHovered = mouseInOldCanvas is { } mp && blockRect.Contains(mp);

                if (showGrips)
                {
                    // Only the outer edges of a midnight-crossing row resize it: the first
                    // slice's top and the last slice's bottom (the cut at midnight isn't an edge).
                    double gripH = Math.Clamp(segH * 0.25, 4, 8);
                    if (b.Whole is null || b.StartLocal == b.Whole.StartLocal)
                    {
                        topGrip = BuildDragGrip(top: true, gripH);
                        content.Children.Add(topGrip);
                        if (alreadyHovered) ((Border)topGrip.Child).Opacity = 0.85;
                    }
                    if (b.Whole is null || b.EndLocal == b.Whole.EndLocal)
                    {
                        botGrip = BuildDragGrip(top: false, gripH);
                        content.Children.Add(botGrip);
                        if (alreadyHovered) ((Border)botGrip.Child).Opacity = 0.85;
                    }
                }
                if (deleteBtn is not null && alreadyHovered) deleteBtn.Opacity = 0.8;

                block.Child = content;

                Canvas.SetLeft(block, cx - barW / 2);
                Canvas.SetTop(block, top);
                canvas.Children.Add(block);
                allDayBlockElements.Add((b, block));
                if (!isLive && !b.Unscheduled && !string.IsNullOrEmpty(b.Id))
                {
                    if (!rowElements.TryGetValue(b.Id, out var pieces)) rowElements[b.Id] = pieces = new();
                    pieces.Add((day, block));
                }
                if (draggable) draggableDayBlockElements.Add((b, block, topGrip, botGrip));
                if (isLive)
                {
                    _liveTickBlockElement = block;
                    _liveTickDay = day;
                    _liveTickTimeLabel = timeLabel;
                    _liveTickBlockLabeled = segH >= 16 && resolved is not null;
                }

                if (topGrip is not null || botGrip is not null)
                    WireGripHoverFade(block, topGrip, botGrip);
                if (deleteBtn is not null)
                {
                    WireDeleteHoverFade(block, deleteBtn);
                    var capturedTarget = b;
                    double delLeft = cx - barW / 2, delTop = top, delW = barW, delH = segH;
                    deleteBtn.MouseLeftButtonDown += (_, e) =>
                    {
                        e.Handled = true; // don't let this reach the block's own MouseLeftButtonDown (BeginMove)
                        ShowDeleteConfirm(capturedTarget, delLeft, delTop, delW, delH);
                    };
                }
                blockRects.Add(blockRect);
            }

            if (AiOn && aiByDay.TryGetValue(day, out var aiDay))
                AddAiCalendarLane(canvas, aiDay, day, colLeft + geom.AiLeft, geom.AiW, blockRects);

            // Second pass: wire up move/resize now that every block for the day has a Border,
            // so a resize's neighbour-push can look up an already-built element to mutate. Sorted
            // once here and handed to every Begin* call for the day, rather than each closure
            // re-sorting its own copy.
            var sortedAllDayBlocks = allDayBlockElements.OrderBy(x => x.Block.StartLocal).ToList();
            foreach (var (b, element, topGrip, botGrip) in draggableDayBlockElements)
            {
                var capturedDay = day;
                var capturedBlock = b;
                var capturedElement = element;

                if (topGrip is not null)
                    topGrip.MouseLeftButtonDown += (_, e) => BeginResize(e, topGrip, top: true, capturedBlock, capturedElement, capturedDay, sortedAllDayBlocks);
                if (botGrip is not null)
                    botGrip.MouseLeftButtonDown += (_, e) => BeginResize(e, botGrip, top: false, capturedBlock, capturedElement, capturedDay, sortedAllDayBlocks);
                element.MouseLeftButtonDown += (_, e) => BeginMove(e, capturedElement, capturedBlock, capturedDay);
            }

            var labelStack = new StackPanel
            {
                Width = dayW, Cursor = System.Windows.Input.Cursors.Hand,
                Background = Brushes.Transparent,
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
                Text = day.ToString("%d"), FontSize = 9.5, TextAlignment = TextAlignment.Center, Foreground = faintBrush,
            });
            var dayForClick = day;
            labelStack.MouseLeftButtonUp += (_, _) => OpenDayDetail(dayForClick);
            Canvas.SetLeft(labelStack, colLeft);
            Canvas.SetTop(labelStack, 4);
            headerCanvas.Children.Add(labelStack);

            // Unscheduled ("by amount") entries for this day — small chips stacked below the
            // day name/number, capped at maxChipRows with a "+N more" summary beyond that (see
            // chipAreaH above, which reserves exactly this many rows for every column).
            var dayUnscheduled = byDay[day].Where(b => b.Unscheduled).ToList();
            for (int ci = 0; ci < Math.Min(dayUnscheduled.Count, maxChipRows); ci++)
            {
                bool overflowRow = ci == maxChipRows - 1 && dayUnscheduled.Count > maxChipRows;
                var ub = dayUnscheduled[ci];
                var resolvedU = A.Tracker.FindByBlock(ub);
                var chipColor = resolvedU is not null ? ColorUtil.Parse(resolvedU.Color) : ((SolidColorBrush)faintBrush).Color;

                var chip = new Border
                {
                    Width = Math.Max(20, dayW - 8), Height = chipRowH - 3,
                    CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(Color.FromArgb(55, chipColor.R, chipColor.G, chipColor.B)),
                    BorderBrush = new SolidColorBrush(chipColor), BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand,
                    ToolTip = overflowRow
                        ? $"{dayUnscheduled.Count - maxChipRows + 1} more unscheduled entries that day"
                        : $"{(resolvedU?.Code ?? ub.ProjectCode)} · {ub.DurationHours:0.0}h · no specific time",
                };
                chip.Child = new TextBlock
                {
                    Text = overflowRow ? $"+{dayUnscheduled.Count - maxChipRows + 1} more" : $"{(resolvedU?.Code ?? ub.ProjectCode)} · {ub.DurationHours:0.#}h",
                    FontSize = 8.5, FontWeight = FontWeights.SemiBold, Foreground = dimBrush,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                };
                Canvas.SetLeft(chip, colLeft + 4);
                Canvas.SetTop(chip, xAxisH + ci * chipRowH);
                if (!overflowRow)
                {
                    var capturedUnscheduled = ub;
                    chip.MouseLeftButtonUp += (_, e) =>
                    {
                        e.Handled = true;
                        bool changed = ShowTimeDialog(() => AddTimeDialog.AskEdit(this, capturedUnscheduled));
                        if (changed) { _cachedWeekStart = null; RenderCurrentWeek(0); }
                    };
                }
                headerCanvas.Children.Add(chip);
            }
        }

        // Current-time indicator — a thin accent-coloured line at "now", only drawn when the
        // displayed week actually includes today (a past/future week has no "now" to mark). Full
        // width, not just today's column, matching how the hour gridlines already span the whole
        // grid — it's a horizontal ruler for "this Y = now" rather than a today-specific marker
        // (the accent-tinted column highlight above already covers that). Added last, after every
        // block, so it draws on top of them. Captured into _liveTickNowLine so OnTick can slide it
        // down in place once a second rather than triggering a full rebuild just to move a line.
        if (days.Contains(today))
        {
            double nowY = DateTime.Now.TimeOfDay.TotalHours * CalendarPxPerHour;
            var nowLine = new Line
            {
                X1 = yAxisW, X2 = width, Y1 = nowY, Y2 = nowY,
                Stroke = accentBrush, StrokeThickness = 1.5, SnapsToDevicePixels = true,
            };
            canvas.Children.Add(nowLine);
            _liveTickNowLine = nowLine;
        }

        // ==================== drag interaction (edge-resize / whole-block move) ====================
        // Tiny handles fade in on hover; a captured drag mutates Canvas.Top/Height directly on the
        // affected Border(s) for smooth 1:1 tracking with live snapping (no rebuild mid-drag — see
        // the _drag guards on OnTick/OnStateChanged/ChartCard_SizeChanged/Navigate), and persists
        // via CsvLog.UpdateBlock once, on release. Local functions (not instance methods) so they
        // can close over this render's canvas/geometry directly.

        const double MinBlockPx = 8; // an edge/neighbour can never be pushed/shrunk below this

        Border BuildDragGrip(bool top, double height) => new()
        {
            Height = height, Background = Brushes.Transparent, Cursor = Cursors.SizeNS,
            VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Bottom,
            Child = new Border
            {
                Width = 22, Height = Math.Min(3, height), CornerRadius = new CornerRadius(1.5),
                Background = Brushes.White, Opacity = 0, IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };

        void WireGripHoverFade(Border block, Border? topGrip, Border? botGrip)
        {
            void Fade(Border? grip, double to) =>
                ((Border?)grip?.Child)?.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(140))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            block.MouseEnter += (_, _) => { Fade(topGrip, 0.85); Fade(botGrip, 0.85); };
            // Guarded: mid-drag the cursor can end up outside the (now resized) block's bounds —
            // the handle being actively dragged shouldn't disappear out from under the mouse.
            block.MouseLeave += (_, _) => { if (_drag is null) { Fade(topGrip, 0); Fade(botGrip, 0); } };
        }

        void WireDeleteHoverFade(Border block, Border delBtn)
        {
            void Fade(double to) => delBtn.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(140))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            block.MouseEnter += (_, _) => Fade(0.8);
            // Stays visible while its own confirm popup is open, same reasoning as the grips' guard.
            block.MouseLeave += (_, _) => { if (_drag is null && !_confirmingDelete) Fade(0); };
        }

        double YToMinutes(double y) => y / CalendarPxPerHour * 60.0;
        double MinutesToY(double minutes) => minutes / 60.0 * CalendarPxPerHour;
        double SnapTo(double rawMinutes, int step) => Math.Round(rawMinutes / step) * (double)step;

        // ---- One snapping set for drags, resizes and drawing ----
        // Times snap to the drag grid, then pull onto a "magnet" when one is within a few pixels:
        // the edge of any other block in view (including midnight cuts) and "now" (so a block can
        // be drawn or dragged right up to the current time).
        var magnets = new List<(DateTime T, string Id)>();
        static DateTime TruncMin(DateTime t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerMinute);
        foreach (var list in byDay.Values)
            foreach (var mb in list)
            {
                if (mb.Unscheduled) continue;
                magnets.Add((TruncMin(mb.StartLocal), mb.Id));
                magnets.Add((TruncMin(mb.EndLocal), mb.Id));
            }
        magnets.Add((TruncMin(DateTime.Now), ""));
        const double SnapMagnetPx = 7;
        double MagnetMinutes() => SnapMagnetPx / CalendarPxPerHour * 60.0;

        // Nearest magnet to `t` within reach (ignoring the row being dragged), else null.
        DateTime? NearestMagnet(DateTime t, string? excludeId, out double distMin)
        {
            DateTime? best = null;
            distMin = double.MaxValue;
            double reach = MagnetMinutes();
            foreach (var (mt, id) in magnets)
            {
                if (excludeId is not null && id == excludeId) continue;
                double dist = Math.Abs((mt - t).TotalMinutes);
                if (dist <= reach && dist < distMin) { best = mt; distMin = dist; }
            }
            return best;
        }

        DateTime SnapTime(DateTime raw, string? excludeId)
        {
            if (NearestMagnet(raw, excludeId, out _) is DateTime m) return m;
            return raw.Date + TimeSpan.FromMinutes(SnapTo(raw.TimeOfDay.TotalMinutes, Math.Max(1, A.Settings.DragSnapMinutes)));
        }

        // Snaps a span of fixed length by whichever of its two edges has the closer magnet,
        // falling back to the grid for the start.
        DateTime SnapSpanStart(DateTime rawStart, TimeSpan length, string? excludeId)
        {
            var ms = NearestMagnet(rawStart, excludeId, out var ds);
            var me = NearestMagnet(rawStart + length, excludeId, out var de);
            if (ms is not null && (me is null || ds <= de)) return ms.Value;
            if (me is not null) return me.Value - length;
            return SnapTime(rawStart, excludeId);
        }

        // Minutes-on-`day` flavour for the pixel-based resize code.
        double SnapMinutesOn(DateTime day, double rawMinutes, string? excludeId)
            => (SnapTime(day.AddMinutes(rawMinutes), excludeId) - day).TotalMinutes;

        // Shared by the move-drag's MouseMove (to withhold any visual movement/snapping until a
        // press has actually left click range) and MouseLeftButtonUp (to tell a genuine drag from
        // a stray click) — see both use sites for the full story.
        const double ClickThresholdPx = 3;

        // ==================== drag-to-add (empty space) ====================
        // A click-drag on blank grid space (as opposed to _drag above, which moves/resizes an
        // EXISTING block) draws a highlight between the anchor point and wherever the mouse
        // currently is — up OR down from the anchor, whichever the user drags — and releasing it
        // opens AddTimeDialog pre-filled to that span. Guarded by _addDragging (see its remarks)
        // so the once-a-second live-tick rebuild can't tear the highlight out mid-drag. State is
        // plain local closures, not instance fields, the same way openConfirm/_confirmingDelete
        // split their own state above — the bool guard alone is enough to survive across
        // MouseMove/MouseUp, since nothing rebuilds this canvas while it's true.
        Border? addHighlight = null;
        TextBlock? addLabel = null;
        double addAnchorY = 0;
        var dragGhosts = new List<Border>(); // preview pieces on other days while a drag runs past midnight
        DateTime addDay = today;
        int addAnchorIdx = 0;
        var addExtras = new List<Border>(); // highlight pieces on the other days of a multi-day draw

        // The span being drawn, from the press point to the current mouse position (which may be
        // on another day column, earlier or later), each end snapped to the drag grid.
        (DateTime Start, DateTime End, int CurIdx) AddSpan(Point mouse)
        {
            int snap = Math.Max(1, A.Settings.DragSnapMinutes);
            int curIdx = Math.Clamp((int)((mouse.X - yAxisW) / dayW), 0, days.Count - 1);
            var a = SnapTime(addDay.AddMinutes(Clamp(YToMinutes(addAnchorY), 0, 24 * 60)), null);
            var c = SnapTime(days[curIdx].AddMinutes(Clamp(YToMinutes(Clamp(mouse.Y, 0, gridHeight)), 0, 24 * 60)), null);
            var st = a < c ? a : c;
            var en = a < c ? c : a;
            if (en <= st)
            {
                en = st.AddMinutes(snap);
                var cap = days[days.Count - 1].AddDays(1);
                if (en > cap) { en = cap; st = en.AddMinutes(-snap); }
            }
            return (st, en, curIdx);
        }

        void UpdateAddPieces(DateTime st, DateTime en)
        {
            if (addHighlight is null) return;
            foreach (var x in addExtras) canvas.Children.Remove(x);
            addExtras.Clear();
            var lastDay = en.TimeOfDay == TimeSpan.Zero ? en.Date.AddDays(-1) : en.Date;
            for (var dd = st.Date; dd <= lastDay; dd = dd.AddDays(1))
            {
                int idx = days.IndexOf(dd);
                if (idx < 0) continue;
                var ps = st > dd ? st : dd;
                var pe = en < dd.AddDays(1) ? en : dd.AddDays(1);
                Border piece;
                if (dd == addDay) piece = addHighlight;
                else
                {
                    piece = new Border
                    {
                        Width = barW, Background = addHighlight.Background, BorderBrush = addHighlight.BorderBrush,
                        BorderThickness = addHighlight.BorderThickness, CornerRadius = addHighlight.CornerRadius,
                        IsHitTestVisible = false,
                    };
                    Canvas.SetZIndex(piece, 500);
                    canvas.Children.Add(piece);
                    addExtras.Add(piece);
                }
                double pTop = MinutesToY((ps - dd).TotalMinutes), pBot = MinutesToY((pe - dd).TotalMinutes);
                Canvas.SetLeft(piece, yAxisW + dayW * idx + geom.HoursCx - barW / 2);
                Canvas.SetTop(piece, pTop);
                piece.Height = Math.Max(1, pBot - pTop);
            }
        }

        // Snaps both edges to the configured drag-snap grid (same one block-resize/move uses),
        // then guarantees a non-zero span by nudging the end forward a step if it ever rounds
        // down onto the start — a live preview during a short drag should never show "12:00–12:00".
        (TimeSpan Start, TimeSpan End) SnappedAddRange(double yTop, double yBottom)
        {
            int snap = Math.Max(1, A.Settings.DragSnapMinutes);
            double startMinutes = Clamp(SnapTo(YToMinutes(yTop), snap), 0, 24 * 60 - snap);
            double endMinutes = Clamp(SnapTo(YToMinutes(yBottom), snap), snap, 24 * 60);
            if (endMinutes <= startMinutes) endMinutes = Math.Min(24 * 60, startMinutes + snap);
            return (TimeSpan.FromMinutes(startMinutes), TimeSpan.FromMinutes(endMinutes));
        }

        // Final safety net, checked once at commit time against EVERY other block that day (not
        // just the one immediate neighbour the live clamp bounds were computed from) — belt and
        // suspenders on top of the live clamping, so an overlapping row can never actually be
        // written even if some edge case in that clamp math is wrong. `except`/`alsoExcept` let
        // the dragged block and (for a resize) its pushed neighbour skip checking against
        // themselves/each other, since both their positions are simultaneously changing.
        bool OverlapsAnyOtherBlock(DateTime newStart, DateTime newEnd, TimeBlock self, TimeBlock? alsoExcept)
        {
            // Checked against what's on disk across every day the span touches (a row can cross
            // midnight), skipping the rows being rewritten by their stored id.
            var selfId = (self.Whole ?? self).Id;
            var otherId = alsoExcept is null ? null : (alsoExcept.Whole ?? alsoExcept).Id;
            var lastDay = newEnd.TimeOfDay == TimeSpan.Zero ? newEnd.Date.AddDays(-1) : newEnd.Date;
            foreach (var other in A.Log.ReadRange(newStart.Date, lastDay < newStart.Date ? newStart.Date : lastDay))
            {
                if (other.Unscheduled || other.Id == selfId || (otherId is not null && other.Id == otherId)) continue;
                if (newStart < other.EndLocal && newEnd > other.StartLocal) return true;
            }
            return liveBlock is not null && !ReferenceEquals(self, liveBlock)
                && newStart < liveBlock.EndLocal && newEnd > liveBlock.StartLocal;
        }

        // Only the live block is a hard stop for a resize's neighbour lookup; a midnight-crossing
        // slice is pushed like any other (its real edge is on the parent row).
        bool IsDraggableBlock(TimeBlock nb, DateTime day) => !ReferenceEquals(nb, liveBlock);

        // Re-lays-out every rendered piece of a stored row (except `except`) for the span it would
        // have if the drag ended now, so both halves of a midnight split move together.
        void SyncPieces(string rowId, DateTime spanStart, DateTime spanEnd, Border? except)
        {
            if (!rowElements.TryGetValue(rowId, out var pieces)) return;
            foreach (var (pieceDay, el) in pieces)
            {
                if (ReferenceEquals(el, except)) continue;
                var ps = spanStart > pieceDay ? spanStart : pieceDay;
                var pe = spanEnd < pieceDay.AddDays(1) ? spanEnd : pieceDay.AddDays(1);
                if (pe <= ps) { el.Visibility = Visibility.Collapsed; continue; }
                el.Visibility = Visibility.Visible;
                Canvas.SetTop(el, MinutesToY((ps - pieceDay).TotalMinutes));
                el.Height = Math.Max(3, MinutesToY((pe - ps).TotalMinutes));
            }
        }

        // The stored span the dragged row would get if released now (shared by the live preview
        // and the final write). `mouseY` is only used by the whole-row shift of a crossing row.
        (DateTime Start, DateTime End) DragSpan(DragState d, double mouseY)
        {
            var row = d.Block.Whole ?? d.Block;
            double top = Canvas.GetTop(d.Element), height = d.Element.Height;
            if (d.IsResize)
                return (d.ResizeTop ? d.Day + TimeSpan.FromMinutes(YToMinutes(top)) : row.StartLocal,
                        d.ResizeTop ? row.EndLocal : d.Day + TimeSpan.FromMinutes(YToMinutes(top + height)));
            if (d.Block.Whole is not null)
            {
                // Whole-row shift: slide the stored row by the drag distance (and any day change),
                // snapped so its start lands on the grid; each day's piece re-derives from that.
                int snapMin = Math.Max(1, A.Settings.DragSnapMinutes);
                var raw = row.StartLocal + (d.CurrentDay - d.Day) + TimeSpan.FromMinutes(YToMinutes(mouseY - d.StartMouseY));
                var ns = SnapSpanStart(raw, row.EndLocal - row.StartLocal, row.Id);
                return (ns, row.EndLocal + (ns - row.StartLocal));
            }
            return (d.CurrentDay + TimeSpan.FromMinutes(YToMinutes(top)), d.CurrentDay + TimeSpan.FromMinutes(YToMinutes(top + height)));
        }

        // The pushed neighbour's stored span; only an edge that actually moved is rewritten.
        (bool Changed, DateTime Start, DateTime End) NeighborSpan(DragState d)
        {
            if (d.NeighborElement is null || d.Neighbor is null) return (false, default, default);
            var nRow = d.Neighbor.Whole ?? d.Neighbor;
            double nTop = Canvas.GetTop(d.NeighborElement), nHeight = d.NeighborElement.Height;
            // Exact equality is fine here (not a fragile float comparison) — the "not
            // encroaching" branch assigns these back from NeighborOrigTop/Height verbatim.
            bool topMoved = nTop != d.NeighborOrigTop;
            bool botMoved = nTop + nHeight != d.NeighborOrigTop + d.NeighborOrigHeight;
            return (topMoved || botMoved,
                topMoved ? d.Day + TimeSpan.FromMinutes(YToMinutes(nTop)) : nRow.StartLocal,
                botMoved ? d.Day + TimeSpan.FromMinutes(YToMinutes(nTop + nHeight)) : nRow.EndLocal);
        }

        // A dropped bar may run into a midnight-crossing row and push its edge (the crossing row
        // shrinks, carrying the change across midnight); any other overlap is still refused.
        // Returns false if the drop can't be made to fit; `pushes` gets the rows to rewrite.
        bool TryPlanMovePushes(TimeBlock self, DateTime newStart, DateTime newEnd,
            List<(TimeBlock Row, DateTime Start, DateTime End)> pushes)
        {
            var selfId = (self.Whole ?? self).Id;
            var lastDay = newEnd.TimeOfDay == TimeSpan.Zero ? newEnd.Date.AddDays(-1) : newEnd.Date;
            foreach (var o in A.Log.ReadRange(newStart.Date, lastDay < newStart.Date ? newStart.Date : lastDay))
            {
                if (o.Unscheduled || o.Id == selfId) continue;
                if (!(newStart < o.EndLocal && newEnd > o.StartLocal)) continue;
                var w = o.Whole;
                if (w is null) return false; // an ordinary single-day block — no pushing
                if (pushes.Any(x => x.Row.Id == w.Id)) continue;
                DateTime ns = w.StartLocal, ne = w.EndLocal;
                if (newStart <= w.StartLocal && newEnd < w.EndLocal) ns = newEnd;
                else if (newStart > w.StartLocal && newEnd >= w.EndLocal) ne = newStart;
                else return false; // swallows it whole, or lands strictly inside it
                if (ne <= ns || ns.Year != w.StartLocal.Year || ns.Month != w.StartLocal.Month) return false;
                pushes.Add((w, ns, ne));
            }
            return !(liveBlock is not null && !ReferenceEquals(self, liveBlock)
                && newStart < liveBlock.EndLocal && newEnd > liveBlock.StartLocal);
        }

        void BeginResize(MouseButtonEventArgs e, Border grip, bool top, TimeBlock block, Border element,
            DateTime day, List<(TimeBlock Block, Border Element)> dayBlocks)
        {
            if (_drag is not null) return;
            CloseConfirm();
            e.Handled = true;
            canvas.CaptureMouse();

            double origTop = Canvas.GetTop(element);
            double origHeight = element.Height;
            int idx = dayBlocks.FindIndex(x => ReferenceEquals(x.Element, element));
            var neighborPair = top
                ? (idx > 0 ? dayBlocks[idx - 1] : ((TimeBlock, Border)?)null)
                : (idx >= 0 && idx < dayBlocks.Count - 1 ? dayBlocks[idx + 1] : ((TimeBlock, Border)?)null);
            // A non-draggable neighbour (the live block, or the rare block spanning past
            // midnight) is never pushed — it's a hard stop instead. dayBlocks here is the FULL
            // per-day list (not just the draggable subset), specifically so this lookup still
            // finds — and correctly stops at — one of those, rather than skipping past it to
            // whatever draggable block happens to be next and allowing an invisible overlap.
            bool neighborIsHardStop = neighborPair is not null && !IsDraggableBlock(neighborPair.Value.Item1, day);

            // A piece of a midnight-crossing row can be pushed to nothing: the rest of that row
            // lives on the neighbouring day, so the push carries across midnight (and back).
            bool canVanish = neighborPair is { } vp && !neighborIsHardStop && vp.Item1.Whole is not null;
            double nMin = canVanish ? 0 : MinBlockPx;
            double low, high, neighborOrigTop = 0, neighborOrigHeight = 0;
            if (top)
            {
                high = origTop + origHeight - MinBlockPx;
                if (neighborPair is { } np)
                {
                    neighborOrigTop = Canvas.GetTop(np.Item2);
                    neighborOrigHeight = np.Item2.Height;
                    low = neighborIsHardStop ? neighborOrigTop + neighborOrigHeight : neighborOrigTop + nMin;
                }
                else low = -gridHeight; // no neighbour: may run up into the previous day
            }
            else
            {
                low = origTop + MinBlockPx;
                if (neighborPair is { } np)
                {
                    neighborOrigTop = Canvas.GetTop(np.Item2);
                    neighborOrigHeight = np.Item2.Height;
                    high = neighborIsHardStop ? neighborOrigTop : neighborOrigTop + neighborOrigHeight - nMin;
                }
                else high = gridHeight * 2; // no neighbour: may run down into the next day
            }
            if (low > high) low = high; // degenerate — neighbour's already at its own minimum; pin rather than invert

            _drag = new DragState
            {
                IsResize = true, ResizeTop = top, Block = block, Element = element, Day = day, CurrentDay = day,
                StartMouseY = e.GetPosition(canvas).Y, OrigTop = origTop, OrigHeight = origHeight,
                LowBound = low, HighBound = high,
                Neighbor = neighborIsHardStop ? null : neighborPair?.Item1,
                NeighborElement = neighborIsHardStop ? null : neighborPair?.Item2,
                NeighborOrigTop = neighborOrigTop, NeighborOrigHeight = neighborOrigHeight,
                NeighborCanVanish = canVanish && !neighborIsHardStop,
            };
        }

        void BeginMove(MouseButtonEventArgs e, Border element, TimeBlock block, DateTime day)
        {
            if (_drag is not null) return;
            CloseConfirm();
            e.Handled = true;
            canvas.CaptureMouse();

            double origTop = Canvas.GetTop(element);
            double origHeight = element.Height;

            // No longer clamped between the immediate previous/next block that day — a whole-
            // block move can now be dragged past its neighbours into any open gap further up or
            // down (or, per CurrentDay below, onto a different day entirely). The only remaining
            // bound is the day's own 0..24h span; whether the FINAL drop position actually lands
            // somewhere free is checked once at MouseUp (OverlapsAnyOtherBlock), same safety net
            // a resize already relies on — nothing is written to disk if it doesn't clear that.
            double low = 0;
            double high = gridHeight - origHeight;
            // A slice of a midnight-crossing row moves the whole row, so it may slide partly off
            // its own day (the other half grows); the real result is computed at MouseUp.
            { low = MinBlockPx - origHeight; high = gridHeight - MinBlockPx; }
            if (low > high) low = high;

            _drag = new DragState
            {
                IsResize = false, ResizeTop = false, Block = block, Element = element, Day = day, CurrentDay = day,
                StartMouseY = e.GetPosition(canvas).Y, OrigTop = origTop, OrigHeight = origHeight,
                LowBound = low, HighBound = high,
                StartMouseX = e.GetPosition(canvas).X, OrigLeft = Canvas.GetLeft(element),
            };
        }

        canvas.MouseMove += (_, e) =>
        {
            if (_addDragging && addHighlight is not null)
            {
                var (previewStart, previewEnd, _) = AddSpan(e.GetPosition(canvas));
                UpdateAddPieces(previewStart, previewEnd);

                if (addLabel is not null)
                {
                    // The hour count only joins the time range once there's comfortably enough
                    // room for it — same "don't cram a second piece of info onto a sliver"
                    // reasoning BuildChart/BuildCalendar's own block labels already follow
                    // (compare their own ASN/time second-line thresholds).
                    bool multiDay = previewEnd > previewStart.Date.AddDays(1);
                    string timeText = multiDay
                        ? $"{previewStart:ddd HH:mm}–{previewEnd:ddd HH:mm}"
                        : $"{previewStart:HH:mm}–{previewEnd:HH:mm}";
                    const double HoursLabelMinHeight = 34; // needs room for a second line, not just wider text
                    addLabel.Text = addHighlight.Height >= HoursLabelMinHeight
                        ? $"{timeText}\n{(previewEnd - previewStart).TotalHours:0.0}h"
                        : timeText;
                    addLabel.Visibility = addHighlight.Height >= 16 ? Visibility.Visible : Visibility.Collapsed;
                }
                return;
            }

            if (_drag is not { } d)
            {
                // Hover-only affordance: a small "+" (see CustomCursors — deliberately smaller
                // than the stock Cursors.Cross, and distinct from the resize grips' SizeNS so the
                // two gestures don't look identical) invites the drag-to-add gesture above over
                // blank grid space; the axis strip gets the plain arrow back since clicking there
                // does nothing. Blocks/grips/etc. override this with their OWN Cursor (the nearer
                // element wins hit-testing), so this only ever actually shows where there's
                // genuinely nothing else to interact with yet.
                canvas.Cursor = e.GetPosition(canvas).X < yAxisW ? Cursors.Arrow : CustomCursors.SmallPlus;
                return;
            }
            double deltaY = e.GetPosition(canvas).Y - d.StartMouseY;

            if (d.IsResize)
            {
                int snap = Math.Max(1, A.Settings.DragSnapMinutes);
                if (d.ResizeTop)
                {
                    double newTop = Clamp(MinutesToY(SnapMinutesOn(d.Day, YToMinutes(d.OrigTop + deltaY), (d.Block.Whole ?? d.Block).Id)), d.LowBound, d.HighBound);
                    Canvas.SetTop(d.Element, newTop);
                    d.Element.Height = Math.Max(MinBlockPx, d.OrigTop + d.OrigHeight - newTop);
                    if (d.NeighborElement is not null)
                    {
                        double neighborOrigBottom = d.NeighborOrigTop + d.NeighborOrigHeight;
                        // Only shrink the neighbour (its bottom edge tracking ours) once we've
                        // actually encroached past its original bottom edge — dragging AWAY
                        // (growing the gap between us) restores it to its untouched original
                        // height instead of dragging it along for the ride.
                        d.NeighborElement.Height = newTop < neighborOrigBottom
                            ? Math.Max(d.NeighborCanVanish ? 0 : MinBlockPx, newTop - d.NeighborOrigTop)
                            : d.NeighborOrigHeight;
                    }
                }
                else
                {
                    double newBottom = Clamp(MinutesToY(SnapMinutesOn(d.Day, YToMinutes(d.OrigTop + d.OrigHeight + deltaY), (d.Block.Whole ?? d.Block).Id)), d.LowBound, d.HighBound);
                    d.Element.Height = Math.Max(MinBlockPx, newBottom - d.OrigTop);
                    if (d.NeighborElement is not null)
                    {
                        if (newBottom > d.NeighborOrigTop)
                        {
                            Canvas.SetTop(d.NeighborElement, newBottom);
                            d.NeighborElement.Height = Math.Max(d.NeighborCanVanish ? 0 : MinBlockPx, d.NeighborOrigTop + d.NeighborOrigHeight - newBottom);
                        }
                        else
                        {
                            // Not encroaching (any more) — put it back exactly as it was rather
                            // than leaving it wherever the last encroaching frame left it.
                            Canvas.SetTop(d.NeighborElement, d.NeighborOrigTop);
                            d.NeighborElement.Height = d.NeighborOrigHeight;
                        }
                    }
                }
            }
            else
            {
                // A whole-block move doesn't touch the element's position at all until the press
                // has actually left click range — otherwise ordinary hardware jitter on what the
                // user experiences as a stationary click was enough to snap the block to the grid
                // for a frame before MouseUp's own click-vs-drag check (below) reverts it via
                // RenderCurrentWeek, reading as a visible "jump" right as the edit dialog opens.
                // A resize grip doesn't get this treatment (see MouseLeftButtonUp's own remarks).
                double totalDx = e.GetPosition(canvas).X - d.StartMouseX;
                if (Math.Abs(deltaY) < ClickThresholdPx && Math.Abs(totalDx) < ClickThresholdPx) return;

                // Cross-day: snap horizontally to whichever day column the cursor is currently
                // over (blocks always sit centred in their column, so there's no fractional-X to
                // preserve — same all-or-nothing snap the double-click-to-add handler uses).
                double mouseX = e.GetPosition(canvas).X;
                int dayIndex = Math.Clamp((int)((mouseX - yAxisW) / dayW), 0, days.Count - 1);
                var targetDay = days[dayIndex];
                // Snap the dropped top (or bottom) edge onto grid/other blocks/now, judged on the
                // day column it's currently over.
                var rawMoveStart = targetDay.AddMinutes(YToMinutes(d.OrigTop + deltaY));
                var snappedMoveStart = SnapSpanStart(rawMoveStart, TimeSpan.FromMinutes(YToMinutes(d.OrigHeight)), (d.Block.Whole ?? d.Block).Id);
                double newTop = Clamp(MinutesToY((snappedMoveStart - targetDay).TotalMinutes), d.LowBound, d.HighBound);
                Canvas.SetTop(d.Element, newTop);
                double targetCx = yAxisW + dayW * dayIndex + geom.HoursCx;
                Canvas.SetLeft(d.Element, targetCx - barW / 2);
                d.CurrentDay = targetDay;
            }

            // Keep the other pieces of a midnight-crossing row (the dragged one and/or the pushed
            // neighbour) in step with the drag, so the split stays visually continuous.
            var rowId = (d.Block.Whole ?? d.Block).Id;
            if (d.Block.Whole is not null)
            {
                var (ds, de) = DragSpan(d, e.GetPosition(canvas).Y);
                // Same-day move: re-lay-out the dragged piece too (it may be clipped/shrunk at
                // midnight); across columns it just follows the cursor.
                SyncPieces(rowId, ds, de, d.IsResize || d.CurrentDay != d.Day ? d.Element : null);
            }
            if (d.Neighbor is not null)
            {
                d.NeighborElement!.Visibility = d.NeighborElement.Height < 1 ? Visibility.Collapsed : Visibility.Visible;
                if (d.Neighbor.Whole is not null)
                {
                    var (nChanged, ns, ne) = NeighborSpan(d);
                    var span = nChanged ? (ns, ne) : (d.Neighbor.Whole.StartLocal, d.Neighbor.Whole.EndLocal);
                    SyncPieces(d.Neighbor.Id, span.Item1, span.Item2, d.NeighborElement);
                }
            }

            // Hide whatever hangs past this column's 0..24h; the ghost below shows it on its own day.
            double clipTop = Math.Max(0, -Canvas.GetTop(d.Element));
            double clipBot = Math.Min(d.Element.Height, gridHeight - Canvas.GetTop(d.Element));
            d.Element.Clip = clipTop > 0 || clipBot < d.Element.Height
                ? new RectangleGeometry(new Rect(-2, clipTop, barW + 4, Math.Max(0, clipBot - clipTop)))
                : null;

            // Preview the part of the dragged span that falls on other days (a block pulled past
            // midnight has no rendered piece there yet).
            foreach (var g in dragGhosts) canvas.Children.Remove(g);
            dragGhosts.Clear();
            var (gs, ge) = DragSpan(d, e.GetPosition(canvas).Y);
            var elementDay = d.IsResize ? d.Day : d.CurrentDay;
            var lastGhostDay = ge.TimeOfDay == TimeSpan.Zero ? ge.Date.AddDays(-1) : ge.Date;
            for (var gd = gs.Date; gd <= lastGhostDay; gd = gd.AddDays(1))
            {
                int gi = days.IndexOf(gd);
                if (gi < 0 || gd == elementDay) continue;
                if (rowElements.TryGetValue(rowId, out var existing) && existing.Any(x => x.Day == gd)) continue;
                var ps = gs > gd ? gs : gd;
                var pe = ge < gd.AddDays(1) ? ge : gd.AddDays(1);
                var ghost = new Border
                {
                    Width = barW, Background = d.Element.Background, CornerRadius = new CornerRadius(4),
                    Opacity = 0.85, IsHitTestVisible = false,
                };
                double gTop = MinutesToY((ps - gd).TotalMinutes), gBot = MinutesToY((pe - gd).TotalMinutes);
                Canvas.SetLeft(ghost, yAxisW + dayW * gi + geom.HoursCx - barW / 2);
                Canvas.SetTop(ghost, gTop);
                ghost.Height = Math.Max(2, gBot - gTop);
                Canvas.SetZIndex(ghost, 500);
                canvas.Children.Add(ghost);
                dragGhosts.Add(ghost);
            }
        };

        canvas.MouseLeftButtonUp += (_, e) =>
        {
            if (_addDragging)
            {
                canvas.ReleaseMouseCapture();
                _addDragging = false;
                if (addHighlight is not null) canvas.Children.Remove(addHighlight);
                foreach (var x in addExtras) canvas.Children.Remove(x);
                addExtras.Clear();

                // A near-zero-movement press/release is a stray click, not a drag — same
                // click-vs-drag distinction the block-move handler below makes for itself.
                const double AddClickThresholdPx = 3;
                var (rangeStart, rangeEnd, releaseIdx) = AddSpan(e.GetPosition(canvas));
                double releaseY = Clamp(e.GetPosition(canvas).Y, 0, gridHeight);
                if (releaseIdx == addAnchorIdx && Math.Abs(releaseY - addAnchorY) < AddClickThresholdPx) return;

                var (clippedStart, clippedEnd) = AddTimeDialog.ClipSpanToEdges(rangeStart, rangeEnd);
                if (clippedEnd <= clippedStart) return; // fully swallowed by one existing block — nothing left to add

                bool added = ShowTimeDialog(() => AddTimeDialog.AskAtTime(this, clippedStart, clippedEnd));
                if (added) { _cachedWeekStart = null; RenderCurrentWeek(0); }
                return;
            }

            foreach (var g in dragGhosts) canvas.Children.Remove(g);
            dragGhosts.Clear();
            if (_drag is { } clipD) clipD.Element.Clip = null;
            if (_drag is not { } d) { canvas.ReleaseMouseCapture(); return; }
            canvas.ReleaseMouseCapture();

            // A whole-block "move" with (near-)zero actual movement is a click, not a drag — open
            // the edit dialog instead of writing back the (unchanged) position. A resize grip is
            // small and single-purpose enough that it doesn't get this treatment.
            var upPos = e.GetPosition(canvas);
            if (!d.IsResize && Math.Abs(upPos.Y - d.StartMouseY) < ClickThresholdPx && Math.Abs(upPos.X - d.StartMouseX) < ClickThresholdPx)
            {
                _drag = null;
                bool changed = ShowTimeDialog(() => AddTimeDialog.AskEdit(this, d.Block));
                if (changed) _cachedWeekStart = null;
                RenderCurrentWeek(0); // also restores the element's position from any sub-threshold jitter
                return;
            }

            var row = d.Block.Whole ?? d.Block;
            var (newStart, newEnd) = DragSpan(d, upPos.Y);
            var (neighborChanged, nStartV, nEndV) = NeighborSpan(d);
            DateTime? neighborNewStart = neighborChanged ? nStartV : null, neighborNewEnd = neighborChanged ? nEndV : null;
            TimeBlock? nRow = d.Neighbor is null ? null : d.Neighbor.Whole ?? d.Neighbor;

            // A cross-MONTH move isn't supported — CsvLog.UpdateBlock rewrites the row in place in
            // the file its ORIGINAL date belongs to, so the new start has to stay within that same
            // file/month (see its remarks). Within the same month, a cross-DAY move is just a
            // normal rewrite with a different date, no different from changing its time-of-day.
            bool crossMonth = newStart.Year != row.StartLocal.Year || newStart.Month != row.StartLocal.Month;

            bool neighborOkSpan = !neighborChanged
                || (neighborNewEnd > neighborNewStart
                    && neighborNewStart!.Value.Year == nRow!.StartLocal.Year && neighborNewStart.Value.Month == nRow.StartLocal.Month);

            var movePushes = new List<(TimeBlock Row, DateTime Start, DateTime End)>();
            bool ok = !crossMonth && newEnd > newStart && neighborOkSpan
                && (d.IsResize
                    ? !OverlapsAnyOtherBlock(newStart, newEnd, d.Block, d.Neighbor)
                    : TryPlanMovePushes(d.Block, newStart, newEnd, movePushes))
                && (!neighborChanged || !OverlapsAnyOtherBlock(neighborNewStart!.Value, neighborNewEnd!.Value, d.Neighbor!, d.Block));

            if (ok)
            {
                var updated = new TimeBlock { StartLocal = newStart, EndLocal = newEnd, Notes = row.Notes, Unscheduled = row.Unscheduled };
                A.Log.UpdateBlock(d.Block, updated);
                foreach (var (pRow, pStart, pEnd) in movePushes)
                    A.Log.UpdateBlock(pRow, new TimeBlock { StartLocal = pStart, EndLocal = pEnd, Notes = pRow.Notes, Unscheduled = pRow.Unscheduled });

                // Only touch the neighbour's row if it was actually pushed — never encroaching
                // on it (e.g. dragging away the whole time) means its row is already correct as
                // recorded, so there's nothing to rewrite.
                if (neighborChanged && d.Neighbor is not null)
                {
                    var nUpdated = new TimeBlock { StartLocal = neighborNewStart!.Value, EndLocal = neighborNewEnd!.Value, Notes = nRow!.Notes, Unscheduled = nRow.Unscheduled };
                    A.Log.UpdateBlock(d.Neighbor, nUpdated);
                }
            }
            // If not ok, nothing is written — the re-render below reverts to the last-saved
            // (necessarily valid) state instead of risking an overlapping row on disk.

            _drag = null;
            _cachedWeekStart = null;
            RenderCurrentWeek(0); // one authoritative re-render, reconciling with what's now on disk
        };

        // Blank grid space (not the axis strip, the day-label row, or on top of an existing
        // block) supports two ways to add a new block: double-click opens AddTimeDialog pre-
        // filled to that day/time with a computed default end (see AskAtTime/SmartDefaultEnd);
        // a single press-drag instead draws a highlight and opens the dialog pre-filled to the
        // dragged span (see the drag-to-add section above, and its MouseMove/MouseLeftButtonUp
        // handling further up this method).
        canvas.Background = Brushes.Transparent; // otherwise empty space isn't hit-test visible at all
        canvas.MouseLeftButtonDown += (_, e) =>
        {
            // Clicking blank canvas space dismisses an open delete confirmation — a click that
            // instead landed on a block/grip/✕/button already had e.Handled set true upstream
            // and never reaches this bubbled handler at all.
            if (openConfirm is not null) CloseConfirm();
            if (_drag is not null) return; // shouldn't happen (blocks capture their own down), but be safe

            var pos = e.GetPosition(canvas);
            if (pos.X < yAxisW) return; // hour-label axis strip
            if (blockRects.Any(r => r.Contains(pos))) return; // on an existing block, not blank space

            int dayIndex = (int)((pos.X - yAxisW) / dayW);
            if (dayIndex < 0 || dayIndex >= days.Count) return;
            var clickedDay = days[dayIndex];

            if (e.ClickCount == 2)
            {
                double clickedMinutes = pos.Y / CalendarPxPerHour * 60;
                double snappedMinutes = Clamp(Math.Round(clickedMinutes / 30.0) * 30.0, 0, 24 * 60 - 30);
                e.Handled = true;
                bool added = ShowTimeDialog(() => AddTimeDialog.AskAtTime(this, clickedDay, TimeSpan.FromMinutes(snappedMinutes)));
                if (added) { _cachedWeekStart = null; RenderCurrentWeek(0); }
                return;
            }

            // Single press — start tracking a potential drag-to-add. Whether this ends up as a
            // genuine drag or just a stray click (including the first half of what's about to
            // become a double-click) is only known at MouseUp — see its own click-vs-drag
            // threshold check.
            e.Handled = true;
            canvas.CaptureMouse();
            _addDragging = true;
            addDay = clickedDay;
            addAnchorY = pos.Y;
            addAnchorIdx = dayIndex;

            double cxForAdd = yAxisW + dayW * dayIndex + geom.HoursCx;
            addLabel = new TextBlock
            {
                FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
                FontFamily = monoFont, IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center, // centres the shorter "2.1h" line under the time range
                Visibility = Visibility.Collapsed,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                { BlurRadius = 3, ShadowDepth = 0, Opacity = 0.6, Color = Colors.Black },
            };
            var ac = ((SolidColorBrush)accentBrush).Color;
            addHighlight = new Border
            {
                Width = barW, Height = 1,
                Background = new SolidColorBrush(Color.FromArgb(70, ac.R, ac.G, ac.B)),
                BorderBrush = accentBrush, BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(4), IsHitTestVisible = false, ClipToBounds = true,
                Child = addLabel,
            };
            Canvas.SetLeft(addHighlight, cxForAdd - barW / 2);
            Canvas.SetTop(addHighlight, pos.Y);
            Canvas.SetZIndex(addHighlight, 500);
            canvas.Children.Add(addHighlight);
        };

        var scroll = new ScrollViewer
        {
            Width = width, Height = bodyHeight, Content = canvas,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        // Ctrl+scroll zooms the hour scale instead of panning — Preview (tunnelling) so this runs,
        // and can mark the event handled, before the ScrollViewer's own bubbling MouseWheel
        // handler would otherwise just scroll it vertically regardless of the modifier key. Shift
        // (week navigation) and plain scroll (hour grid) don't need anything here: Shift is
        // intercepted higher up, at the whole chart area (see ChartArea_PreviewMouseWheel), before
        // tunnelling even reaches this ScrollViewer; plain scroll falls through to this
        // ScrollViewer's own default vertical-scroll handling untouched. Hovering the pinned date
        // header (outside this ScrollViewer entirely) and scrolling there, or hovering the
        // week-range label in the window's own title bar, both reach ChartArea_MouseWheel by
        // ordinary bubbling — no extra wiring needed for either.
        scroll.PreviewMouseWheel += (_, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            e.Handled = true;
            ZoomCalendar(e.Delta > 0 ? 1 : -1, bodyHeight);
        };
        // Remember the offset as the user scrolls, so the once-a-second rebuild (OnTick, growing
        // the live block) can restore it below instead of yanking them back to the default spot.
        // Tracking is deliberately NOT wired until after Loaded applies the initial position —
        // ScrollViewer fires ScrollChanged of its own accord while still settling its initial
        // layout (with VerticalOffset 0, before anything's actually been scrolled), and wiring
        // this up early let that transient 0 stomp the remembered offset on every single
        // once-a-second rebuild, which is what was pulling the view back to the top continuously
        // instead of just on the initial open.
        scroll.Loaded += (_, _) =>
        {
            double target = _calendarScrollOffset
                ?? Clamp(12 * CalendarPxPerHour - bodyHeight / 2, 0, Math.Max(0, gridHeight - bodyHeight));
            scroll.ScrollToVerticalOffset(target);
            _calendarScrollOffset = target;
            scroll.ScrollChanged += (_, e) => _calendarScrollOffset = e.VerticalOffset;
        };

        // Scrollable body (Row 0) above the fixed date header (Row 1), pinned at the bottom of
        // the view.
        var root = new Grid { Width = width, Height = height };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(headerH) });
        Grid.SetRow(scroll, 0);
        Grid.SetRow(headerCanvas, 1);
        root.Children.Add(headerCanvas);
        root.Children.Add(scroll);
        return root;
    }

    /// <summary>
    /// Cross-fades the new view in over the old one, in place — used for the bar/calendar view
    /// toggle, as opposed to SlideIn's horizontal week-to-week slide.
    ///
    /// A true block-level "morph" (each block sliding/resizing from its bar position into its
    /// calendar time-slot, riding on the fact that a day's blocks are already chronological) was
    /// considered instead of a cross-fade, and is architecturally sound but not quite a drop-in:
    /// BuildChart's default grouping (Settings.ChartMergeAllSessions) collapses a whole day's
    /// sessions for a project into ONE bar segment even when there's a real gap between them
    /// (worked 9-10am, then again 2-3pm) — so a segment doesn't always have a single contiguous
    /// rectangle to morph into; morphing its bounding box would paint over the untracked gap as
    /// if it were worked time, which is actively misleading, not just visually rough. The correct
    /// fix is for the bar view's departure frame specifically to render one sub-element per raw
    /// block, stacked with zero gap within a merged group (pixel-identical to today's look, since
    /// same-project blocks touching with no gap are indistinguishable from one solid bar) so each
    /// has its own well-defined start/end to animate toward. That's a real, scoped follow-up
    /// rather than something to bolt on blind — a cross-fade is the documented, safe stand-in.
    /// </summary>
    private void CrossFade(FrameworkElement newVisual)
    {
        var old = _currentChart;
        _currentChart = newVisual;

        newVisual.Opacity = 0;
        ChartHost.Children.Add(newVisual);
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        CacheWhileAnimating(newVisual, fadeIn);
        newVisual.BeginAnimation(OpacityProperty, fadeIn);

        if (old is null) return;
        old.CacheMode = NewTransitionCache();
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        fadeOut.Completed += (_, _) => ChartHost.Children.Remove(old);
        old.BeginAnimation(OpacityProperty, fadeOut);
    }

    // A week/view transition moves or fades the whole chart every frame. Without a cache WPF
    // re-renders the subtree each frame, which is cheap for Totals but not for Calendar (clipped
    // rounded blocks, drop-shadow effects, a ScrollViewer over a 24h canvas — each an offscreen
    // pass under the animated opacity). Rendering it once to a bitmap and moving that keeps the
    // slide smooth; the cache comes off when it lands so text goes back to crisp.
    private BitmapCache NewTransitionCache() => new() { SnapsToDevicePixels = true };

    private void CacheWhileAnimating(FrameworkElement visual, AnimationTimeline anim)
    {
        visual.CacheMode = NewTransitionCache();
        // The live block's pulse would dirty the cache every frame, defeating it — hold it still
        // for the ~quarter second the transition takes.
        foreach (var (_, _, clock) in _breathing) clock.Controller?.Pause();
        anim.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_currentChart, visual)) return;
            visual.CacheMode = null;
            foreach (var (_, _, clock) in _breathing) clock.Controller?.Resume();
        };
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
            var slideIn = new DoubleAnimation(tt.X, 0, TimeSpan.FromMilliseconds(280)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            CacheWhileAnimating(newVisual, slideIn);
            tt.BeginAnimation(TranslateTransform.XProperty, slideIn);
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
        old.CacheMode = NewTransitionCache();
        var slideOut = new DoubleAnimation(0, -direction * dist, TimeSpan.FromMilliseconds(280))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        slideOut.Completed += (_, _) => ChartHost.Children.Remove(old);
        oldTt.BeginAnimation(TranslateTransform.XProperty, slideOut);
        old.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(240)));
    }

    private void RebuildLegend(IEnumerable<TimeBlock> blocks, string period)
    {
        Legend.Children.Clear();
        // Resolved by Id (not raw ProjectCode) so a project renamed mid-week still shows up for
        // days it was tracked under its old code — see TrackerService.FindByBlock.
        var activeIds = new HashSet<string>(
            blocks.Select(b => A.Tracker.FindByBlock(b)?.Id).OfType<string>(),
            StringComparer.Ordinal);

        foreach (var p in A.Tracker.Projects)
        {
            if (!activeIds.Contains(p.Id)) continue;
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
                Text = $"No tracked time this {period}.", FontSize = 12, Foreground = (Brush)FindResource("TextFaint"),
            });
        }
    }
}
