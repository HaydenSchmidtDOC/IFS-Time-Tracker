using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace TimeTracker.App.Tutorial;

/// <summary>
/// Orchestrates the guided tour: owns the ordered step list, drives navigation between MainWindow
/// and TimesheetWindow, resolves each step's target element, creates/shows the overlay for it, and
/// runs the calendar-gesture ghost demos. A fresh instance is created per run (see
/// App.StartTutorial) — there's no persistent state worth keeping between tours.
/// </summary>
internal sealed class TutorialController
{
    private static App A => App.Current;

    private readonly List<TutorialStep> _steps;
    private int _index;
    private TutorialOverlayWindow? _overlay;
    private TutorialHost _currentHost = TutorialHost.None;
    private DispatcherTimer? _pendingTimer;
    private bool _running;

    /// <summary>The user's actual default-view preference, captured on Start and restored on
    /// Finish/Skip — steps 5 and 8 force Totals then Calendar to demonstrate both (see their own
    /// OnEnter), and SelectView persists that switch exactly like a real click would, so without
    /// this a completed tour would silently leave the user's saved default on whichever view the
    /// tour happened to end its demo on, rather than whatever they'd actually chosen before.</summary>
    private string? _originalDefaultView;

    public TutorialController() => _steps = BuildSteps();

    public void Start()
    {
        if (_running) return;
        _running = true;
        _index = 0;
        _originalDefaultView = A.Settings.DefaultTimesheetView;
        // Ensure there's actually something to point the first arrow at — StartMinimized (tray/
        // pill only) would otherwise leave nothing on screen for the tour to anchor to.
        A.ShowMainWindow();
        _currentHost = TutorialHost.MainWindow;
        RenderStep(_steps[0], 0);
    }

    private void Next()
    {
        if (_index >= _steps.Count - 1) { Finish(); return; }
        ShowStepAt(_index + 1);
    }

    private void Back()
    {
        if (_index <= 0) return;
        ShowStepAt(_index - 1);
    }

    private void Finish()
    {
        if (!_running) return;
        _running = false;
        _pendingTimer?.Stop();
        _pendingTimer = null;
        _overlay?.Close();
        _overlay = null;
        // Always leave the app on the main window, tracking untouched — Skip and Finish share
        // this same cleanup since neither should ever leave the user mid-tour with a half-open
        // timesheet window or a dimmed screen.
        if (A.IsTimesheetsVisible) A.CloseTimesheets(); else A.ShowMainWindow();

        // Undo the view-switching side effect of steps 5/8 demonstrating both views (see
        // _originalDefaultView's own remarks) — the window itself self-corrects on its next open
        // (AnimateOpenFrom always re-reads Settings.DefaultTimesheetView fresh), so restoring the
        // persisted setting here is the only piece that needs undoing by hand.
        if (_originalDefaultView is { } view) A.Settings.DefaultTimesheetView = view;

        A.Settings.OnboardingPromptShown = true;
        A.Store.SaveSettings(A.Settings);
    }

    // ==================== step transitions ====================

    private void ShowStepAt(int index)
    {
        _index = index;
        _pendingTimer?.Stop();
        _pendingTimer = null;
        // NOT closing _overlay here (see RenderStep, which now closes the OUTGOING one only after
        // the incoming one is already up) — closing it first used to leave a real gap with nothing
        // blocking input, and the delay below makes that gap worse, not better: a real click/drag
        // landing on the real window during it could leave that window's own drag-to-add mouse
        // capture stuck (its MouseLeftButtonUp lands on nothing, since neither the old overlay,
        // already gone, nor the new one, not built yet, is there to receive it).

        var step = _steps[index];
        bool hostChanged = step.Host != _currentHost;
        if (hostChanged) EnsureHostVisible(step.Host);
        _currentHost = step.Host;
        step.OnEnter?.Invoke();

        if (hostChanged)
        {
            // AnimateOpenFrom/AnimateCloseTo (TimesheetWindow) settle their own cosmetic
            // scale/opacity over up to ~220ms after Show()/Hide() already returns — resolving a
            // target element's on-screen rect before that finishes could capture a mid-animation
            // (still-scaling) position. A short fixed delay is simpler and more robust here than
            // threading a completion callback through App.Open/CloseTimesheets (neither exposes
            // one today, and both are reused/idempotent entry points other callers rely on too);
            // it reads as an unremarkable beat before the next card appears, not a stall. The
            // OUTGOING overlay stays up (stale card and all) for the whole wait, still fully
            // blocking input — see RenderStep and this method's own remarks above.
            _pendingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
            _pendingTimer.Tick += (_, _) =>
            {
                _pendingTimer!.Stop();
                _pendingTimer = null;
                RenderStep(step, index);
            };
            _pendingTimer.Start();
        }
        else
        {
            RenderStep(step, index);
        }
    }

    /// <summary>Makes whichever window a step targets the one actually on screen — the single
    /// place this decision is made, so Back works exactly like Next rather than needing each step
    /// to separately know how to undo the one before it. TimesheetWindow steps just (re-)open it
    /// (a no-op beyond an Activate() if it's already showing); every other step closes it back to
    /// main if it's currently the one visible, or just (re-)shows main otherwise.</summary>
    private void EnsureHostVisible(TutorialHost host)
    {
        if (host == TutorialHost.TimesheetWindow) A.OpenTimesheets();
        else if (A.IsTimesheetsVisible) A.CloseTimesheets();
        else A.ShowMainWindow();
    }

    private void RenderStep(TutorialStep step, int index)
    {
        Window host = step.Host == TutorialHost.TimesheetWindow ? A.EnsureTimesheets() : A.MainWindowRef;
        var target = step.ResolveTarget?.Invoke();

        var newOverlay = new TutorialOverlayWindow(host, step, index, _steps.Count, target);
        newOverlay.NextRequested += Next;
        newOverlay.BackRequested += Back;
        newOverlay.SkipRequested += Finish;
        newOverlay.Show();
        newOverlay.Activate();

        // Only NOW does the outgoing overlay (still showing the previous step, if any) come down
        // — the new one is already up and already blocking input by this point, so there is never
        // a frame where neither is: see ShowStepAt's own remarks on why that gap matters.
        var outgoing = _overlay;
        _overlay = newOverlay;
        outgoing?.Close();

        if (step.Ghost != GhostDemo.None && _overlay.GhostLayer is { } layer)
        {
            var cleanup = BuildGhostDemo(step.Ghost, layer);
            if (cleanup is not null) _overlay.Closed += (_, _) => cleanup();
        }
    }

    // ==================== step sequence ====================

    private static List<TutorialStep> BuildSteps() =>
    [
        new TutorialStep
        {
            Host = TutorialHost.MainWindow,
            ResolveTarget = () => A.MainWindowRef.AddProjectBtn,
            Title = "Add your first project",
            Body = "Everything you track is logged against a project — a code, an ASN, and a colour. Add one to get started.",
        },
        new TutorialStep
        {
            Host = TutorialHost.MainWindow,
            ResolveTarget = () => A.MainWindowRef.ProjectList,
            Title = "Pick a project, then press start",
            Body = "Select a project and press the round ▶ button (or just double-click it) to start tracking. It becomes Stop/Switch while a session runs, and the timer above counts up live.",
        },
        new TutorialStep
        {
            Host = TutorialHost.MainWindow,
            Title = "Jump between projects fast",
            Body = "Press Ctrl+↑ or Ctrl+↓ anywhere, any time, to pop up a quick switcher and jump straight to another project — no need to open this window at all.",
        },
        new TutorialStep
        {
            Host = TutorialHost.MainWindow,
            AnchorBottomRight = true,
            Title = "Tray icon",
            Body = "That's your tray icon down there. Hidden under the ^ arrow? Drag it out to pin it to your taskbar.",
        },
        new TutorialStep
        {
            Host = TutorialHost.MainWindow,
            ResolveTarget = () => A.MainWindowRef.TimesheetsBtn,
            Title = "See where your time went",
            Body = "The timesheets view shows a full week of tracked time, two different ways. Let's take a look.",
        },
        new TutorialStep
        {
            Host = TutorialHost.TimesheetWindow,
            ResolveTarget = () => A.EnsureTimesheets().ViewToggleHost,
            OnEnter = () => A.EnsureTimesheets().SelectView(calendar: false),
            Title = "Totals and Calendar",
            Body = "Totals stacks up each day's hours by project. Calendar — the other tab here — lays them out across the day instead; we'll switch to it shortly.",
        },
        new TutorialStep
        {
            Host = TutorialHost.TimesheetWindow,
            ResolveTarget = () => A.EnsureTimesheets().MergeSessionsToggleHost,
            Title = "Merge sessions",
            Body = "By default, every session logged for a project on the same day merges into one bar. Turn this off to see each individual session split out instead.",
        },
        new TutorialStep
        {
            Host = TutorialHost.TimesheetWindow,
            ResolveTarget = () => A.EnsureTimesheets().TimesheetSettingsBtn,
            Title = "Display options",
            Body = "This gear icon holds the calendar's drag-snap granularity, the minimum split-block size, and how aggressively small segments fold together in Totals view.",
        },
        new TutorialStep
        {
            Host = TutorialHost.TimesheetWindow,
            ResolveTarget = () => A.EnsureTimesheets().ViewToggleHost,
            OnEnter = () => A.EnsureTimesheets().SelectView(calendar: true),
            Title = "The Calendar view",
            Body = "Calendar lays your day out by time-of-day instead of totals — and it's where you can add, move, resize, and delete time directly. Let's try each of those.",
        },
        new TutorialStep
        {
            Host = TutorialHost.TimesheetWindow,
            ResolveTarget = () => A.EnsureTimesheets().ChartHost,
            Ghost = GhostDemo.Add,
            Title = "Drag to add time",
            Body = "Click and drag across empty space to block out time — or just double-click a spot to add a quick entry there.",
        },
        new TutorialStep
        {
            Host = TutorialHost.TimesheetWindow,
            ResolveTarget = () => A.EnsureTimesheets().ChartHost,
            Ghost = GhostDemo.Move,
            Title = "Edit or move a block",
            Body = "Click any block to open and edit it — times, notes, everything. Or drag its body to move it, even onto a different day.",
        },
        new TutorialStep
        {
            Host = TutorialHost.TimesheetWindow,
            ResolveTarget = () => A.EnsureTimesheets().ChartHost,
            Ghost = GhostDemo.Resize,
            Title = "Resize a block",
            Body = "Drag a block's top or bottom edge to change when it starts or ends.",
        },
        new TutorialStep
        {
            Host = TutorialHost.TimesheetWindow,
            ResolveTarget = () => A.EnsureTimesheets().ChartHost,
            Ghost = GhostDemo.Delete,
            Title = "Delete a block",
            Body = "Hover a block and click the ✕ that appears, then confirm — gone.",
        },
        new TutorialStep
        {
            Host = TutorialHost.MainWindow,
            Title = "You're all set",
            Body = "That's the whole tour. Add your first project below to get started — you can replay this any time from Settings.",
        },
    ];

    // ==================== ghost demos ====================
    // Mime the four calendar gestures over the (real, but otherwise untouched) empty ChartHost
    // area with translucent phantom shapes — nothing here ever touches the real canvas or writes
    // to the CSV log. Every animation in a given demo shares the same Duration (varying only
    // BeginTime to stagger their appearance) so their relative phase stays locked forever: a
    // RepeatBehavior.Forever clock's BeginTime only offsets its very first cycle, not the repeats
    // after — two clocks of equal period stay in whatever relative phase they started in.

    /// <summary>Fake px-per-hour scale used only to turn a ghost block's on-screen geometry into
    /// plausible clock-time text — mirrors TimesheetWindow's own default CalendarPxPerHour zoom
    /// (60), not because the ghost layer is actually laid out on a 24h grid (it isn't; its
    /// coordinates are arbitrary fractions of ChartHost's size), but so the numbers move at a
    /// speed that looks like the same gesture on the real calendar.</summary>
    private const double GhostPxPerHour = 60;

    /// <summary>Mirrors BuildCalendar's own yAxisW/dayW/barW math exactly (see
    /// TimesheetWindow.BuildCalendar) so a ghost block lines up with the REAL day-column grid
    /// underneath it instead of floating at some arbitrary fraction of the layer's width — the
    /// bug an earlier version of these demos had. ChartHost's own ScrollViewer never shows a
    /// horizontal scrollbar (HorizontalScrollBarVisibility="Disabled"), so its ActualWidth already
    /// matches the real calendar canvas's width closely enough for the two to line up.</summary>
    private const double GhostYAxisW = 40;
    private static double GhostDayW(Canvas layer) => Math.Max(10, (layer.Width - GhostYAxisW) / 7.0);
    private static double GhostBarW(Canvas layer) => Math.Max(20, GhostDayW(layer) - 10);
    private static double GhostColumnLeft(Canvas layer, int dayIndex) =>
        GhostYAxisW + GhostDayW(layer) * dayIndex + GhostDayW(layer) / 2 - GhostBarW(layer) / 2;

    /// <summary>Each Ghost* method returns a cleanup action when it hooked CompositionTarget.Rendering
    /// for a live-updating label (Add/Move/Resize) — that event is process-wide, not scoped to any
    /// one window, so RenderStep wires this into the overlay's own Closed event to unsubscribe it;
    /// otherwise every step's ghost demo would keep ticking forever, stacking on top of every
    /// subsequent one. Delete's label is static (nothing moves), so it returns null — nothing to
    /// clean up beyond the overlay window itself.</summary>
    private static Action? BuildGhostDemo(GhostDemo kind, Canvas layer) => kind switch
    {
        GhostDemo.Add => GhostAdd(layer),
        GhostDemo.Move => GhostMove(layer),
        GhostDemo.Resize => GhostResize(layer),
        GhostDemo.Delete => GhostDelete(layer),
        _ => null,
    };

    private static Action? GhostAdd(Canvas layer)
    {
        double blockW = GhostBarW(layer);
        double x = GhostColumnLeft(layer, 3); // Thursday — a safely mid-week column
        double top = layer.Height * 0.28;
        double bottom = Math.Min(layer.Height * 0.9, top + layer.Height * 0.42);
        const double startHour = 10.0;

        var block = MakePhantomBlock();
        block.Width = blockW;
        var label = AddGhostLabel(block);
        Canvas.SetLeft(block, x);
        Canvas.SetTop(block, top);
        layer.Children.Add(block);

        var cursor = MakeCursorDot();
        Canvas.SetLeft(cursor, x + blockW / 2 - cursor.Width / 2);
        Canvas.SetTop(cursor, top + 6 - cursor.Height / 2); // matches the Y animation's own "from" below, so it doesn't flash at the layer's origin during BeginTime
        layer.Children.Add(cursor);

        var dur = TimeSpan.FromMilliseconds(1100);
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var pause = TimeSpan.FromMilliseconds(300); // brief hold at the "collapsed" state each loop

        block.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(6, bottom - top, dur)
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease, BeginTime = pause });
        cursor.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(top + 6 - cursor.Height / 2, bottom - cursor.Height / 2, dur)
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease, BeginTime = pause });

        // Mirrors the real drag-to-add preview (see TimesheetWindow's own addLabel/SnappedAddRange)
        // — the time range, and once there's room a duration line under it, recomputed every
        // rendered frame off the block's CURRENTLY animated height, exactly like the real preview
        // recomputes off the live drag position rather than a value fixed at drag-start.
        EventHandler onRender = (_, _) => label.Text = FormatGhostTime(startHour, block.ActualHeight / GhostPxPerHour, block.ActualHeight);
        CompositionTarget.Rendering += onRender;
        return () => CompositionTarget.Rendering -= onRender;
    }

    private static Action? GhostMove(Canvas layer)
    {
        double blockW = GhostBarW(layer);
        double blockH = Math.Min(60, layer.Height * 0.22);
        double x1 = GhostColumnLeft(layer, 2), x2 = GhostColumnLeft(layer, 4); // Wed -> Fri, a real column-to-column move
        double y1 = layer.Height * 0.22, y2 = layer.Height * 0.55;
        const double durationHours = 0.75, dayTopHour = 8.0; // fake "top of the visible range" anchor

        var block = MakePhantomBlock();
        block.Width = blockW; block.Height = blockH;
        var label = AddGhostLabel(block);
        Canvas.SetLeft(block, x1);
        Canvas.SetTop(block, y1);
        layer.Children.Add(block);

        var cursor = MakeCursorDot();
        // Matches the two animations' own "from" values below, so it doesn't flash at the layer's
        // origin during the initial BeginTime delay.
        Canvas.SetLeft(cursor, x1 + blockW / 2 - cursor.Width / 2);
        Canvas.SetTop(cursor, y1 + blockH / 2 - cursor.Height / 2);
        layer.Children.Add(cursor);

        var dur = TimeSpan.FromMilliseconds(1200);
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var pause = TimeSpan.FromMilliseconds(400);

        block.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(x1, x2, dur)
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease, BeginTime = pause });
        block.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(y1, y2, dur)
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease, BeginTime = pause });

        cursor.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(x1 + blockW / 2 - cursor.Width / 2, x2 + blockW / 2 - cursor.Width / 2, dur)
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease, BeginTime = pause });
        cursor.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(y1 + blockH / 2 - cursor.Height / 2, y2 + blockH / 2 - cursor.Height / 2, dur)
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease, BeginTime = pause });

        // Duration stays fixed (a move never changes how long the block is) — only the start (and
        // so end) time ticks as the block's own animated Top position slides, read fresh each frame
        // the same way GhostAdd reads ActualHeight.
        EventHandler onRender = (_, _) =>
        {
            double startHour = dayTopHour + Math.Max(0, Canvas.GetTop(block)) / GhostPxPerHour;
            label.Text = FormatGhostTime(startHour, durationHours, block.ActualHeight);
        };
        CompositionTarget.Rendering += onRender;
        return () => CompositionTarget.Rendering -= onRender;
    }

    private static Action? GhostResize(Canvas layer)
    {
        double blockW = GhostBarW(layer);
        double x = GhostColumnLeft(layer, 3);
        double top = layer.Height * 0.2;
        double h1 = layer.Height * 0.18, h2 = layer.Height * 0.5;
        const double startHour = 10.0;

        var block = MakePhantomBlock();
        block.Width = blockW;
        var label = AddGhostLabel(block);
        Canvas.SetLeft(block, x);
        Canvas.SetTop(block, top);
        layer.Children.Add(block);

        var cursor = MakeCursorDot();
        Canvas.SetLeft(cursor, x + blockW / 2 - cursor.Width / 2);
        Canvas.SetTop(cursor, top + h1 - cursor.Height / 2); // matches the Y animation's own "from" below
        layer.Children.Add(cursor);

        var dur = TimeSpan.FromMilliseconds(1200);
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var pause = TimeSpan.FromMilliseconds(400);

        block.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(h1, h2, dur)
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease, BeginTime = pause });
        cursor.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(top + h1 - cursor.Height / 2, top + h2 - cursor.Height / 2, dur)
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease, BeginTime = pause });

        EventHandler onRender = (_, _) => label.Text = FormatGhostTime(startHour, block.ActualHeight / GhostPxPerHour, block.ActualHeight);
        CompositionTarget.Rendering += onRender;
        return () => CompositionTarget.Rendering -= onRender;
    }

    private static Action? GhostDelete(Canvas layer)
    {
        double blockW = GhostBarW(layer);
        double blockH = Math.Min(56, layer.Height * 0.2);
        double x = GhostColumnLeft(layer, 3), y = layer.Height * 0.26;

        var block = MakePhantomBlock();
        block.Width = blockW; block.Height = blockH;
        var label = AddGhostLabel(block);
        label.Text = FormatGhostTime(10.0, blockH / GhostPxPerHour, blockH); // static — nothing moves in this demo
        Canvas.SetLeft(block, x);
        Canvas.SetTop(block, y);
        layer.Children.Add(block);

        var deleteX = new TextBlock
        {
            Text = "✕", FontSize = 9.5, FontWeight = FontWeights.Bold, Foreground = Brushes.White, Opacity = 0,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 3, ShadowDepth = 0, Opacity = 0.7, Color = Colors.Black },
        };
        Canvas.SetLeft(deleteX, x + blockW - 16);
        Canvas.SetTop(deleteX, y + 3);
        layer.Children.Add(deleteX);

        var cursor = MakeCursorDot();
        Canvas.SetLeft(cursor, x + blockW - 24);
        Canvas.SetTop(cursor, y - 30); // matches the Y animation's own "from" below
        layer.Children.Add(cursor);

        var confirmCard = BuildMiniConfirmCard();
        confirmCard.Opacity = 0;
        Canvas.SetLeft(confirmCard, x);
        Canvas.SetTop(confirmCard, y + blockH + 10);
        layer.Children.Add(confirmCard);

        // Cursor's own period (2 x 900ms = 1800ms) is an exact multiple of the two fades' period
        // (2 x 300ms = 600ms), so despite the different Durations all three stay in a stable
        // repeating relationship rather than drifting — see this method group's own remarks.
        cursor.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(y - 30, y + 3 - cursor.Height / 2 + 6, TimeSpan.FromMilliseconds(900))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
        deleteX.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromMilliseconds(500) });
        confirmCard.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromMilliseconds(1000) });
        return null; // static label, nothing animated off a live frame — no CompositionTarget hook to clean up
    }

    /// <summary>A small time-range label on a phantom block, styled after — and positioned like —
    /// the real calendar block's own time text (see BuildCalendar's timeLabel) and the drag-to-add
    /// preview's addLabel, so the phantom genuinely reads as "a time block", not just a coloured
    /// rectangle. A Border can only have one Child; ghost blocks don't need the real block's grips/
    /// delete-✕ Grid, so the label can just BE the Child directly rather than needing that Grid.</summary>
    private static TextBlock AddGhostLabel(Border block)
    {
        var label = new TextBlock
        {
            FontSize = 9, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
            Margin = new Thickness(5, 3, 0, 0), VerticalAlignment = VerticalAlignment.Top,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 3, ShadowDepth = 0, Opacity = 0.6, Color = Colors.Black },
        };
        block.Child = label;
        return label;
    }

    /// <summary>Formats a fake time range the same way the real calendar does — "HH:mm–HH:mm",
    /// plus a second "0.0h" duration line once the block is tall enough to comfortably hold it
    /// (mirrors BuildCalendar's own segH >= 34 threshold for showing that second line at all).</summary>
    private static string FormatGhostTime(double startHours, double durationHours, double blockHeightPx)
    {
        string range = $"{FormatClock(startHours)}–{FormatClock(startHours + durationHours)}";
        return blockHeightPx >= 34 ? $"{range}\n{durationHours:0.0}h" : range;
    }

    private static string FormatClock(double hours)
    {
        var t = TimeSpan.FromHours(Math.Max(0, hours));
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}";
    }

    private static Border MakePhantomBlock()
    {
        // AccentColor is a plain Color resource (not a Brush) kept specifically for cases like
        // this that need to build their own alpha-blended brush from it rather than paint with the
        // accent at full opacity — see Dark.xaml/Light.xaml's own remarks on that key.
        var c = (Color)Application.Current.FindResource("AccentColor");
        return new Border
        {
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromArgb(110, c.R, c.G, c.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(210, c.R, c.G, c.B)),
            BorderThickness = new Thickness(1.5),
        };
    }

    private static Ellipse MakeCursorDot() => new()
    {
        Width = 12, Height = 12,
        Fill = Brushes.White,
        Stroke = new SolidColorBrush(Color.FromArgb(220, 20, 24, 32)),
        StrokeThickness = 2,
        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 4, ShadowDepth = 0, Opacity = 0.5, Color = Colors.Black },
    };

    private static Border BuildMiniConfirmCard()
    {
        // Mirrors the real inline delete-confirm card's own styling (see TimesheetWindow's
        // ShowDeleteConfirm) — Surface2 background, "Live"-tinted title text — closely enough to
        // read as a preview of the real thing rather than a generic tooltip.
        var text = new TextBlock { Text = "Delete this block?", FontSize = 10.5, FontWeight = FontWeights.Medium };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Live");
        var card = new Border { Padding = new Thickness(8, 6, 8, 6), CornerRadius = new CornerRadius(6), Child = text };
        card.SetResourceReference(Border.BackgroundProperty, "Surface2");
        card.Effect = new System.Windows.Media.Effects.DropShadowEffect
        { BlurRadius = 8, ShadowDepth = 1, Opacity = 0.3, Color = Color.FromRgb(0x16, 0x1C, 0x2A) };
        return card;
    }
}
