using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using TimeTracker.App.UI;
using TimeTracker.Core;

namespace TimeTracker.App;

/// <summary>
/// Manually add a block of tracked time — for when a window of work was never started/stopped
/// live. Two entry points (see the static Ask* methods): the "+" button above the timesheet
/// chart (defaults to today, "Amount" mode), and double-clicking blank space in the calendar
/// view (defaults to the clicked day/time, "Times" mode, pre-filled).
///
/// "Amount" and "Times" are genuinely different kinds of record, not just two ways to fill in
/// the same fields: Times commits to an exact start/end and is validated against that day's
/// other clock-positioned blocks; Amount is a day+project+duration total with no clock time at
/// all (see TimeBlock.Unscheduled) — it never needs validating against anything, since it
/// doesn't occupy a slot anything else could collide with.
///
/// The dialog itself only collects and validates — it does not touch the log directly. The
/// actual CsvLog.Append and the optional note prompt happen in the static Ask*/Commit path AFTER
/// the dialog has closed, so NotePrompt shows as its own top-level dialog rather than nested on
/// top of this one, and so this class stays a plain "collect input" dialog like
/// AddProjectDialog/NotePrompt.
/// </summary>
public partial class AddTimeDialog : Window
{
    private static App A => App.Current;

    private DateTime _day; // local date this block is being added to
    private Project? _selectedProject;
    private SegmentedToggle _modeToggle = null!;
    private (DateTime Start, DateTime End)? _result;
    /// <summary>Where Commit() actually writes to — normally just [_result], but a Times-mode
    /// entry that cleanly spans one or more whole existing blocks (see TryParseTimesMode) splits
    /// into one segment per gap around them instead of a single row.</summary>
    private List<(DateTime Start, DateTime End)> _resultSegments = new();
    private bool _deleteConfirmed;
    private bool _closing; // guards against Deactivated re-entering Close() — see the ctor

    /// <summary>A fresh add with no explicit end defaults to this long — see SmartDefaultEnd,
    /// which shortens it when something else starts sooner.</summary>
    private const int DefaultDurationMinutes = 60;

    /// <summary>True once the user has actually typed into EndTimeBox themselves (as opposed to
    /// it holding a value THIS dialog computed) — see OnStartTimeEdited, which only auto-follows
    /// the start time while this is false.</summary>
    private bool _endManuallyEdited;
    /// <summary>Guards EndTimeBox's own TextChanged from mistaking one of this dialog's OWN writes
    /// to the box (the auto-follow in OnStartTimeEdited) for the user actually typing into it.</summary>
    private bool _syncingTimes;

    /// <summary>Non-null when this dialog is correcting an existing block rather than adding a
    /// new one — see AskEdit. Editing never shows the Amount/Times toggle — it shows whichever
    /// panel actually matches the block's own kind (Amount for an unscheduled entry, Times for a
    /// real one), since "convert between the two" isn't something this dialog offers.</summary>
    private TimeBlock? _editingBlock;

    private AddTimeDialog(DateTime day, int initialMode, TimeSpan? prefillStart, TimeSpan? prefillEnd,
        TimeBlock? editingBlock = null, Project? preselectProject = null)
    {
        InitializeComponent();
        _day = day.Date;
        _editingBlock = editingBlock;

        _modeToggle = new SegmentedToggle("Amount", "Times", initialMode);
        _modeToggle.SelectionChanged += _ => RefreshModeVisibility();
        ModeToggleHost.Content = _modeToggle.Root;

        // Fields must hold their defaults BEFORE the first RefreshModeVisibility()/RefreshAmountHint()
        // call below reads them — otherwise the initial hint briefly shows "enter a duration" against
        // still-empty boxes. Editing an unscheduled block previews its OWN duration, not the
        // usual new-entry default.
        int defaultMinutes = _editingBlock is { Unscheduled: true } eb
            ? Math.Max(1, (int)Math.Round(eb.DurationSeconds / 60.0))
            : DefaultDurationMinutes;
        AmountHoursBox.Text = (defaultMinutes / 60).ToString();
        AmountMinutesBox.Text = (defaultMinutes % 60).ToString();
        AmountHoursBox.TextChanged += (_, _) => { if (_modeToggle.SelectedIndex == 0) RefreshAmountHint(); };
        AmountMinutesBox.TextChanged += (_, _) => { if (_modeToggle.SelectedIndex == 0) RefreshAmountHint(); };

        var start = prefillStart ?? DefaultStartTime();
        var end = prefillEnd ?? SmartDefaultEnd(start);
        StartTimeBox.Text = Fmt(start);
        EndTimeBox.Text = Fmt(end);
        // Wired AFTER the initial Text assignments above so the dialog's own starting values
        // never mark the end as "manually edited" — only the user actually typing does that.
        // Only meaningful for a fresh add: correcting an existing block's recorded times (see
        // _editingBlock) never auto-follows, so those two boxes just hold whatever the user types
        // into each independently, same as before this feature existed.
        if (_editingBlock is null)
        {
            StartTimeBox.TextChanged += (_, _) => OnStartTimeEdited();
            EndTimeBox.TextChanged += (_, _) =>
            {
                if (_syncingTimes) return;
                _endManuallyEdited = true;
                RefreshTimesHint();
            };
        }

        BuildProjectRows(preselectProject);
        RefreshDayText();

        if (_editingBlock is not null)
        {
            TitleText.Text = "Edit time";
            AddButton.Content = "Save";
            DayNavGrid.Visibility = Visibility.Collapsed;
            ModeLabel.Visibility = Visibility.Collapsed;
            ModeToggleHost.Visibility = Visibility.Collapsed;
            DeleteRow.Visibility = Visibility.Visible;

            NoteLabel.Visibility = Visibility.Visible;
            NoteBox.Visibility = Visibility.Visible;
            NoteBox.Text = _editingBlock.Notes ?? "";

            bool unscheduled = _editingBlock.Unscheduled;
            AmountPanel.Visibility = unscheduled ? Visibility.Visible : Visibility.Collapsed;
            AmountHintText.Visibility = unscheduled ? Visibility.Visible : Visibility.Collapsed;
            TimesPanel.Visibility = unscheduled ? Visibility.Collapsed : Visibility.Visible;
            if (unscheduled) RefreshAmountHint();
        }
        else
        {
            RefreshModeVisibility();
        }

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        // Click off to dismiss, same light-dismiss feel as the Timesheet settings popover —
        // a real Window doesn't get that for free the way a Popup does, so it's wired by hand:
        // clicking anywhere outside this window (it can't be the owner, which is non-interactive
        // while this is modal) hands focus elsewhere, which is exactly what Deactivated reports.
        // Closing (not Deactivated) is what actually marks _closing — Close() itself deactivates
        // the window as part of shutting down, so without this guard every OTHER way of closing
        // (Cancel, Add, Escape, the delete confirm) re-entered Close() a second time via this
        // same handler and crashed ("...while a Window is closing").
        Closing += (_, _) => _closing = true;
        Deactivated += (_, _) => { if (!_closing) Close(); };
    }

    /// <summary>Open defaulted to today, "Amount" mode. Returns true if a block was added.</summary>
    public static bool Ask(Window owner, DateTime day)
    {
        var d = new AddTimeDialog(day, initialMode: 0, null, null) { Owner = owner };
        d.ShowDialog();
        return d.Commit();
    }

    /// <summary>Open pre-filled to a specific day/start, "Times" mode, end computed the same
    /// smart-default way a fresh Ask() would (see SmartDefaultEnd) — used by the calendar view's
    /// double-click-to-add. Returns true if a block was added.</summary>
    public static bool AskAtTime(Window owner, DateTime day, TimeSpan start)
    {
        var d = new AddTimeDialog(day, initialMode: 1, start, null) { Owner = owner };
        d.ShowDialog();
        return d.Commit();
    }

    /// <summary>Open pre-filled to an EXPLICIT start/end — used by the calendar view's
    /// drag-to-add gesture, where the dragged span (already clipped clear of any block it only
    /// partially overlaps — see ClipDragToEdges) is the whole point, not something to
    /// second-guess with a computed default. Returns true if a block was added.</summary>
    public static bool AskAtTime(Window owner, DateTime day, TimeSpan start, TimeSpan end)
    {
        var d = new AddTimeDialog(day, initialMode: 1, start, end) { Owner = owner };
        d.ShowDialog();
        return d.Commit();
    }

    /// <summary>Open pre-filled to correct an existing block's project/times — used by the
    /// calendar view's click-to-edit. Returns true if the block was changed.</summary>
    public static bool AskEdit(Window owner, TimeBlock block)
    {
        var project = A.Tracker.FindByBlock(block);
        var d = new AddTimeDialog(block.StartLocal.Date, initialMode: block.Unscheduled ? 0 : 1,
            block.StartLocal.TimeOfDay, block.EndLocal.TimeOfDay, editingBlock: block, preselectProject: project)
        { Owner = owner };
        d.ShowDialog();
        return d.Commit();
    }

    /// <summary>Now rounded to the nearest 30 min if adding to today; 09:00 otherwise — a
    /// same-day add is most likely "I forgot to log the last little while", so anchoring near
    /// the present is more useful than always defaulting to the morning.</summary>
    private TimeSpan DefaultStartTime()
    {
        if (_day != DateTime.Today) return TimeSpan.FromHours(9);
        var now = DateTime.Now.TimeOfDay;
        double roundedMinutes = Math.Round(now.TotalMinutes / 30.0) * 30.0;
        return TimeSpan.FromMinutes(Math.Clamp(roundedMinutes, 0, 24 * 60 - 1));
    }

    private static string Fmt(TimeSpan t) => $"{(int)t.TotalHours:00}:{t.Minutes:00}";

    private void RefreshDayText()
    {
        DayText.Text = _day == DateTime.Today ? "Today"
            : _day == DateTime.Today.AddDays(-1) ? "Yesterday"
            : _day == DateTime.Today.AddDays(1) ? "Tomorrow"
            : _day.ToString("ddd, d MMM");
    }

    private void RefreshModeVisibility()
    {
        bool byAmount = _modeToggle.SelectedIndex == 0;
        AmountPanel.Visibility = byAmount ? Visibility.Visible : Visibility.Collapsed;
        AmountHintText.Visibility = byAmount ? Visibility.Visible : Visibility.Collapsed;
        TimesPanel.Visibility = byAmount ? Visibility.Collapsed : Visibility.Visible;
        if (byAmount) RefreshAmountHint(); else RefreshTimesHint();
    }

    /// <summary>Previews what pressing Add/Save will actually record.</summary>
    private void RefreshAmountHint()
    {
        AmountHintText.Text = TryGetAmountMinutes(out var minutes, out var error)
            ? $"Adds {minutes / 60}h {minutes % 60}m to {DayText.Text}'s total for this project — no specific clock time."
            : error;
    }

    /// <summary>Previews a Times-mode entry that cleanly spans one or more whole existing blocks
    /// (see TryParseTimesMode) — silent otherwise, since the plain non-overlapping case needs no
    /// comment and an actual invalid overlap is left to ErrorText, shown only once Add is
    /// pressed. Never shown while editing (_editingBlock) — correcting a single existing block
    /// never produces more than one row.</summary>
    private void RefreshTimesHint()
    {
        if (_editingBlock is not null) { TimesHintText.Visibility = Visibility.Collapsed; return; }
        if (TryParseTimesMode(out _, out _, out var segments, out _) && segments.Count > 1)
        {
            TimesHintText.Text = "Overlaps existing block(s) that will stay as-is — this entry will be split into "
                + $"{segments.Count} separate blocks around them.";
            TimesHintText.Visibility = Visibility.Visible;
        }
        else
        {
            TimesHintText.Visibility = Visibility.Collapsed;
        }
    }

    private void BuildProjectRows(Project? preselect = null)
    {
        ProjectsPanel.Children.Clear();
        var projects = A.Tracker.Projects;
        _selectedProject = preselect ?? A.Tracker.LastActive ?? projects.FirstOrDefault();

        foreach (var p in projects)
            ProjectsPanel.Children.Add(BuildProjectRow(p));

        if (projects.Count == 0)
        {
            ProjectsPanel.Children.Add(new TextBlock
            {
                Text = "No projects yet.", FontSize = 12, Margin = new Thickness(6),
                Foreground = (Brush)FindResource("TextFaint"),
            });
        }
    }

    private UIElement BuildProjectRow(Project p)
    {
        var row = new Border
        {
            CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 6, 8, 6),
            Cursor = Cursors.Hand, Background = Brushes.Transparent,
            Tag = p,
        };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new Ellipse
        {
            Width = 9, Height = 9, Fill = ColorUtil.Brush(p.Color),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
        });
        content.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(p.Name) ? p.Code : $"{p.Code}  ·  {p.Name}",
            FontSize = 12.5, Foreground = (Brush)FindResource("Text"), VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        row.Child = content;

        void Refresh() => row.Background = ReferenceEquals(_selectedProject, p)
            ? Tint(p.Color) : Brushes.Transparent;
        Refresh();

        row.MouseLeftButtonUp += (_, _) =>
        {
            _selectedProject = p;
            foreach (var child in ProjectsPanel.Children)
                if (child is Border { Tag: Project rp } rb) rb.Background = ReferenceEquals(_selectedProject, rp) ? Tint(rp.Color) : Brushes.Transparent;
            if (_modeToggle.SelectedIndex == 0) RefreshAmountHint();
        };
        return row;
    }

    private static Brush Tint(string hex)
    {
        var c = ColorUtil.Parse(hex);
        return new SolidColorBrush(Color.FromArgb(46, c.R, c.G, c.B));
    }

    /// <summary>Local start time of today's live session, if one is running — the one instant
    /// that's actually known about it; its end isn't, since it hasn't happened yet.</summary>
    private static DateTime? LiveSessionStart()
        => A.Tracker.IsRunning && A.Tracker.State.BlockStartUtc is DateTime startUtc ? startUtc.ToLocalTime() : null;

    /// <summary>Drops the seconds component — every time this dialog can show or accept is
    /// whole-minute (see Fmt/TryParseTimesMode), but a block recorded by the live tracker rarely
    /// starts/ends exactly on one (switching projects mid-minute is the common case). Comparing a
    /// freshly-typed whole-minute time against a stored sub-minute one made editing a block
    /// WITHOUT changing its times at all falsely "overlap" its own immediate neighbour: e.g. a
    /// block truly starting 14:45:07 (chained straight off the previous block's stop) displays
    /// and re-parses as 14:45:00 the moment you open Save without touching it — 7 seconds earlier
    /// than its own recorded start, which is enough to collide with whatever ends at 14:45:07
    /// right before it. Truncating every compared boundary to the same whole-minute grid the
    /// dialog itself operates on keeps the comparison apples-to-apples.</summary>
    private static DateTime TruncateToMinute(DateTime d) => new(d.Year, d.Month, d.Day, d.Hour, d.Minute, 0, d.Kind);

    /// <summary>That day's occupied spans — committed, clock-positioned blocks only (excluding
    /// <paramref name="excluding"/>, if any — see AskEdit) plus, for today, the still-running live
    /// session's REAL elapsed range (start → now, not extended any further). Unscheduled ("by
    /// amount") entries never occupy a range at all and are never in this list, on either side of
    /// the comparison — see TimeBlock.Unscheduled.
    ///
    /// Deliberately NOT extended past "now": a Times-mode entry (or a drag) is free to target the
    /// future, including time after a currently-live session's start — that's no longer treated
    /// as unsafe to create. What used to be prevented here (a future entry silently colliding
    /// with wherever a live session eventually grows to) is instead handled live, at the moment
    /// it'd actually happen, by TrackerService's own collision check.
    ///
    /// Static (not just an instance method reading _day/_editingBlock) so the calendar view's
    /// drag-to-add gesture can pre-clip a raw drag span — see ClipDragToEdges — against the SAME
    /// data before a dialog instance even exists yet.</summary>
    private static List<(DateTime Start, DateTime End)> OccupiedRangesFor(DateTime day, TimeBlock? excluding)
    {
        var ranges = A.Log.ReadRange(day, day)
            .Where(b => b.StartLocal.Date == day && !b.Unscheduled && (excluding is null || b.Id != excluding.Id))
            .Select(b => (Start: TruncateToMinute(b.StartLocal), End: TruncateToMinute(b.EndLocal)))
            .ToList();
        if (day == DateTime.Today && LiveSessionStart() is DateTime liveStart)
            ranges.Add((TruncateToMinute(liveStart), TruncateToMinute(DateTime.Now)));
        ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
        return ranges;
    }

    private List<(DateTime Start, DateTime End)> OccupiedRanges() => OccupiedRangesFor(_day, _editingBlock);

    /// <summary>Picks a sensible end for a fresh start time with no explicit end of its own (see
    /// Ask/AskAtTime(start)): exactly <see cref="DefaultDurationMinutes"/> later, unless another
    /// occupied range (a committed block, or today's live session) starts sooner within that
    /// window — in which case the end butts right up against it instead, same idea as
    /// ClipDragToEdges but anchored on a single start rather than a whole dragged span. Clamped
    /// to the end of the day (24:00) — this dialog never spans midnight.</summary>
    private TimeSpan SmartDefaultEnd(TimeSpan start)
    {
        var startDt = _day + start;
        var cap = startDt.AddMinutes(DefaultDurationMinutes);
        var dayEnd = _day.AddDays(1);
        if (cap > dayEnd) cap = dayEnd;

        var nextStart = OccupiedRanges()
            .Select(r => r.Start)
            .Where(s => s > startDt && s < cap)
            .DefaultIfEmpty(cap)
            .Min();
        return nextStart - _day;
    }

    /// <summary>Reacts to the user changing the start time while adding (never while editing —
    /// see the ctor, which only wires this up for a fresh add). If the end hasn't been manually
    /// touched yet, it just follows the new start via SmartDefaultEnd, same as a brand new dialog
    /// would compute. If the end WAS manually set, it's left alone as long as it's still valid —
    /// only a start that's pushed past it forces a re-snap, since the alternative is silently
    /// landing on an unaddable (end-before-start) span. That re-snap counts as a fresh default
    /// again (not "manual" any more), so a further start tweak keeps following it rather than
    /// getting stuck on a value the user never actually chose.</summary>
    private void OnStartTimeEdited()
    {
        if (!TimeSpan.TryParse(StartTimeBox.Text, CultureInfo.InvariantCulture, out var newStart)
            || newStart < TimeSpan.Zero || newStart >= TimeSpan.FromHours(24))
            return; // not a fully-typed valid time yet — wait for one before reacting

        if (_endManuallyEdited
            && TimeSpan.TryParse(EndTimeBox.Text, CultureInfo.InvariantCulture, out var curEnd)
            && curEnd > newStart)
        {
            RefreshTimesHint(); // still valid — untouched, but the overlap preview may have changed
            return;
        }

        _syncingTimes = true;
        EndTimeBox.Text = Fmt(SmartDefaultEnd(newStart));
        _syncingTimes = false;
        _endManuallyEdited = false;
        RefreshTimesHint();
    }

    /// <summary>Validates the typed duration — the entire "by amount" contract now, since it no
    /// longer has a clock placement to compute (see TimeBlock.Unscheduled): no day-boundary math,
    /// no interaction with existing blocks or a live session, because it doesn't occupy a time
    /// range for any of that to matter against.</summary>
    private bool TryGetAmountMinutes(out int minutes, out string? error)
    {
        minutes = (int.TryParse(AmountHoursBox?.Text, out var h) ? h : 0) * 60
                + (int.TryParse(AmountMinutesBox?.Text, out var m) ? m : 0);
        if (minutes <= 0) { error = "Enter a duration greater than zero."; return false; }
        error = null;
        return true;
    }

    /// <summary>Splits [start,end) into the gaps left after removing every (already sorted,
    /// ascending) range in <paramref name="spanned"/> — the "before the first", "between each
    /// pair", and "after the last" pieces, dropping any that come out shorter than
    /// Settings.MinSplitBlockMinutes (e.g. two spanned blocks a minute apart shouldn't produce a
    /// one-minute sliver crammed between them — that gap is just absorbed into whichever
    /// neighbouring block it butts up against instead of becoming a row of its own). A spanned
    /// block sitting exactly flush against one edge of the range likewise leaves nothing on that
    /// side. Handles any number of spanned ranges, including none at all (returns the original
    /// span unchanged) — the same logic covers a plain non-overlapping add, a clean edge-trim,
    /// and a genuine multi-way split without special-casing any of them.</summary>
    private static List<(DateTime Start, DateTime End)> SplitAroundSpanned(
        DateTime start, DateTime end, List<(DateTime Start, DateTime End)> spanned)
    {
        // The plain "rs > cursor"/"end > cursor" zero-length check still applies regardless of
        // the configured minimum — a MinSplitBlockMinutes of 0 means "off" (no minimum beyond
        // that), not "allow an actual zero-length row".
        var minGap = TimeSpan.FromMinutes(Math.Max(0, A.Settings.MinSplitBlockMinutes));
        var segments = new List<(DateTime, DateTime)>();
        var cursor = start;
        foreach (var (rs, re) in spanned)
        {
            if (rs > cursor && rs - cursor >= minGap) segments.Add((cursor, rs));
            if (re > cursor) cursor = re;
        }
        if (end > cursor && end - cursor >= minGap) segments.Add((cursor, end));
        return segments;
    }

    /// <summary>Pre-clips a freshly dragged span (calendar view's drag-to-add — see
    /// TimesheetWindow.BuildCalendar) so it never PARTIALLY overlaps an existing block: whichever
    /// boundary lands INSIDE such a block snaps to that block's near edge instead. A block the
    /// drag fully SPANS (both of ITS edges inside the drag) is left alone here — that's a clean
    /// overlap the dialog itself is fine with, offering to split around it (see
    /// TryParseTimesMode) rather than something to silently trim away. Ranges are processed in
    /// order, each against the boundaries as already adjusted by the previous one, so a drag
    /// brushing two separate blocks (one near each end) resolves against both.</summary>
    public static (TimeSpan Start, TimeSpan End) ClipDragToEdges(DateTime day, TimeSpan start, TimeSpan end)
    {
        var startDt = day + start;
        var endDt = day + end;
        foreach (var (rs, re) in OccupiedRangesFor(day, null))
        {
            bool overlaps = startDt < re && endDt > rs;
            if (!overlaps) continue;
            bool fullyContained = rs >= startDt && re <= endDt;
            if (fullyContained) continue;
            if (startDt >= rs && startDt < re) startDt = re;
            if (endDt > rs && endDt <= re) endDt = rs;
        }
        return (startDt - day, endDt - day);
    }

    /// <summary>Validates the typed Times-mode span and, if valid, returns the actual segment(s)
    /// Commit() should write: just [start,end) for a plain non-overlapping entry, or one segment
    /// per gap around any existing block(s) it cleanly spans (see SplitAroundSpanned) — a block
    /// it only PARTIALLY overlaps is still a hard error, same as always. Also rejects a span
    /// that, once split, would leave nothing to add at all (it exactly matches existing
    /// block(s)).</summary>
    private bool TryParseTimesMode(out DateTime start, out DateTime end,
        out List<(DateTime Start, DateTime End)> segments, out string? error)
    {
        start = end = default;
        segments = new();
        if (!TimeSpan.TryParse(StartTimeBox.Text, CultureInfo.InvariantCulture, out var ts) || ts < TimeSpan.Zero || ts >= TimeSpan.FromHours(24))
        { error = "Enter a valid start time, e.g. 09:00."; return false; }
        if (!TimeSpan.TryParse(EndTimeBox.Text, CultureInfo.InvariantCulture, out var te) || te <= TimeSpan.Zero || te > TimeSpan.FromHours(24))
        { error = "Enter a valid end time, e.g. 17:30."; return false; }

        start = _day + ts;
        end = _day + te;
        if (end <= start) { error = "End must be after start."; return false; }

        var liveStart = LiveSessionStart();
        var spanned = new List<(DateTime Start, DateTime End)>();
        foreach (var (rs, re) in OccupiedRanges())
        {
            if (!(start < re && end > rs)) continue; // no overlap at all
            // Splitting around a spanned block is only offered for a fresh add — Commit() has no
            // path to split an EDIT into multiple rows, so any overlap while editing (even a
            // clean one) is still a hard error, same as before this feature existed.
            bool fullyContained = _editingBlock is null && rs >= start && re <= end;
            if (!fullyContained)
            {
                error = _day == DateTime.Today && liveStart == rs
                    ? $"Overlaps your active session, running since {rs:HH:mm} — try a time before {rs:HH:mm}, or after {re:HH:mm} (right now)."
                    : $"Overlaps an existing block ({rs:HH:mm}–{re:HH:mm}).";
                return false;
            }
            spanned.Add((rs, re));
        }

        segments = SplitAroundSpanned(start, end, spanned);
        if (segments.Count == 0) { error = "This exactly matches an existing block — nothing to add."; return false; }
        error = null;
        return true;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProject is null) { ShowError("Choose a project."); return; }

        (DateTime Start, DateTime End)? span;
        if (_modeToggle.SelectedIndex == 0)
        {
            if (!TryGetAmountMinutes(out var minutes, out var error)) { ShowError(error!); return; }
            // Nominal, midnight-anchored placeholder — see TimeBlock.Unscheduled. Not a real
            // clock position; Commit() is what actually marks it as such.
            span = (_day, _day.AddMinutes(minutes));
            _resultSegments = new() { span.Value };
        }
        else
        {
            if (!TryParseTimesMode(out var s, out var en, out var segments, out var error)) { ShowError(error!); return; }
            span = (s, en);
            _resultSegments = segments;
        }

        _result = span;
        Close();
    }

    /// <summary>Runs after the dialog has closed (see the static Ask* methods). Adding banks the
    /// collected span, prompting for a note first exactly like a normal stop/switch, so a
    /// manually-added block goes through the same configured behaviour as a live one. Editing
    /// rewrites the existing row in place instead — its note comes from NoteBox (pre-filled with
    /// whatever was already recorded, editable rather than re-prompted), and the project can be
    /// reassigned if it was changed in the picker.</summary>
    private bool Commit()
    {
        if (_deleteConfirmed && _editingBlock is not null) return A.Log.DeleteBlock(_editingBlock);
        if (_result is null || _selectedProject is null) return false;
        var (start, end) = _result.Value;
        var project = _selectedProject;
        bool unscheduled = _modeToggle.SelectedIndex == 0;

        if (_editingBlock is not null)
        {
            var updated = new TimeBlock
            {
                ProjectId = project.Id, ProjectCode = project.Code, Asn = project.Asn, ProjectName = project.Name,
                StartLocal = start, EndLocal = end,
                DurationSeconds = (long)(end - start).TotalSeconds,
                Notes = NoteBox.Text.Trim(),
                Unscheduled = unscheduled,
            };
            return A.Log.UpdateBlock(_editingBlock, updated);
        }

        // One note prompt for the whole add, even when it ends up as several rows (a Times-mode
        // entry that cleanly spans existing block(s) — see TryParseTimesMode/_resultSegments):
        // it's one logical action from the user's point of view, and the same note applies to
        // every piece of it.
        var note = A.MaybeAskNote(project);
        foreach (var (segStart, segEnd) in _resultSegments)
        {
            A.Log.Append(new TimeBlock
            {
                Id = Guid.NewGuid().ToString("N"),
                ProjectId = project.Id, ProjectCode = project.Code, Asn = project.Asn, ProjectName = project.Name,
                StartLocal = segStart, EndLocal = segEnd,
                DurationSeconds = (long)(segEnd - segStart).TotalSeconds,
                Notes = note,
                Unscheduled = unscheduled,
            });
        }
        return true;
    }

    private void ShowError(string msg) { ErrorText.Text = msg; ErrorText.Visibility = Visibility.Visible; }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void DeleteLink_Click(object sender, MouseButtonEventArgs e)
    { DeleteLink.Visibility = Visibility.Collapsed; DeleteConfirmRow.Visibility = Visibility.Visible; }
    private void DeleteCancel_Click(object sender, RoutedEventArgs e)
    { DeleteConfirmRow.Visibility = Visibility.Collapsed; DeleteLink.Visibility = Visibility.Visible; }
    private void DeleteConfirm_Click(object sender, RoutedEventArgs e) { _deleteConfirmed = true; Close(); }

    private void Header_Drag(object sender, MouseButtonEventArgs e) { if (e.ChangedButton == MouseButton.Left) DragMove(); }

    private void PrevDay_Click(object sender, RoutedEventArgs e) { _day = _day.AddDays(-1); RefreshDayText(); RefreshModeVisibility(); }
    private void NextDay_Click(object sender, RoutedEventArgs e) { _day = _day.AddDays(1); RefreshDayText(); RefreshModeVisibility(); }
}
