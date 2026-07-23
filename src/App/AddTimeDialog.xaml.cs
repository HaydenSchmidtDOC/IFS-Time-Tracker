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
    private bool _deleteConfirmed;
    private bool _closing; // guards against Deactivated re-entering Close() — see the ctor

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
            : Math.Max(1, A.Settings.ManualBlockMaxMinutes);
        AmountHoursBox.Text = (defaultMinutes / 60).ToString();
        AmountMinutesBox.Text = (defaultMinutes % 60).ToString();
        AmountHoursBox.TextChanged += (_, _) => { if (_modeToggle.SelectedIndex == 0) RefreshAmountHint(); };
        AmountMinutesBox.TextChanged += (_, _) => { if (_modeToggle.SelectedIndex == 0) RefreshAmountHint(); };

        var start = prefillStart ?? DefaultStartTime();
        var end = prefillEnd ?? start.Add(TimeSpan.FromMinutes(Math.Max(1, A.Settings.ManualBlockMaxMinutes)));
        StartTimeBox.Text = Fmt(start);
        EndTimeBox.Text = Fmt(end);

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

    /// <summary>Open pre-filled to a specific day/span, "Times" mode — used by the calendar
    /// view's double-click-to-add. Returns true if a block was added.</summary>
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
        if (byAmount) RefreshAmountHint();
    }

    /// <summary>Previews what pressing Add/Save will actually record.</summary>
    private void RefreshAmountHint()
    {
        AmountHintText.Text = TryGetAmountMinutes(out var minutes, out var error)
            ? $"Adds {minutes / 60}h {minutes % 60}m to {DayText.Text}'s total for this project — no specific clock time."
            : error;
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
    /// the one being edited, if any — see AskEdit) plus, for today, the still-running live
    /// session's REAL elapsed range (start → now, not extended any further). Unscheduled ("by
    /// amount") entries never occupy a range at all and are never in this list, on either side of
    /// the comparison — see TimeBlock.Unscheduled.
    ///
    /// Deliberately NOT extended past "now": a Times-mode entry (or a drag) is free to target the
    /// future, including time after a currently-live session's start — that's no longer treated
    /// as unsafe to create. What used to be prevented here (a future entry silently colliding
    /// with wherever a live session eventually grows to) is instead handled live, at the moment
    /// it'd actually happen, by TrackerService's own collision check.</summary>
    private List<(DateTime Start, DateTime End)> OccupiedRanges()
    {
        var ranges = A.Log.ReadRange(_day, _day)
            .Where(b => b.StartLocal.Date == _day && !b.Unscheduled && (_editingBlock is null || b.Id != _editingBlock.Id))
            .Select(b => (Start: TruncateToMinute(b.StartLocal), End: TruncateToMinute(b.EndLocal)))
            .ToList();
        if (_day == DateTime.Today && LiveSessionStart() is DateTime liveStart)
            ranges.Add((TruncateToMinute(liveStart), TruncateToMinute(DateTime.Now)));
        ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
        return ranges;
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

    private bool TryParseTimesMode(out DateTime start, out DateTime end, out string? error)
    {
        start = end = default;
        if (!TimeSpan.TryParse(StartTimeBox.Text, CultureInfo.InvariantCulture, out var ts) || ts < TimeSpan.Zero || ts >= TimeSpan.FromHours(24))
        { error = "Enter a valid start time, e.g. 09:00."; return false; }
        if (!TimeSpan.TryParse(EndTimeBox.Text, CultureInfo.InvariantCulture, out var te) || te <= TimeSpan.Zero || te > TimeSpan.FromHours(24))
        { error = "Enter a valid end time, e.g. 17:30."; return false; }

        start = _day + ts;
        end = _day + te;
        if (end <= start) { error = "End must be after start."; return false; }

        var liveStart = LiveSessionStart();
        foreach (var (rs, re) in OccupiedRanges())
        {
            if (start < re && end > rs)
            {
                error = _day == DateTime.Today && liveStart == rs
                    ? $"Overlaps your active session, running since {rs:HH:mm} — try a time before {rs:HH:mm}, or after {re:HH:mm} (right now)."
                    : $"Overlaps an existing block ({rs:HH:mm}–{re:HH:mm}).";
                return false;
            }
        }
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
        }
        else
        {
            if (!TryParseTimesMode(out var s, out var en, out var error)) { ShowError(error!); return; }
            span = (s, en);
        }

        _result = span;
        Close();
    }

    /// <summary>Runs after the dialog has closed (see the static Ask* methods). Adding banks the
    /// collected span, prompting for a note first exactly like a normal stop/switch, so a
    /// manually-added block goes through the same configured behaviour as a live one. Editing
    /// rewrites the existing row in place instead — its note is left as recorded rather than
    /// re-prompted, and the project can be reassigned if it was changed in the picker.</summary>
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
                Notes = _editingBlock.Notes,
                Unscheduled = unscheduled,
            };
            return A.Log.UpdateBlock(_editingBlock, updated);
        }

        var note = A.MaybeAskNote(project);
        var block = new TimeBlock
        {
            Id = Guid.NewGuid().ToString("N"),
            ProjectId = project.Id, ProjectCode = project.Code, Asn = project.Asn, ProjectName = project.Name,
            StartLocal = start, EndLocal = end,
            DurationSeconds = (long)(end - start).TotalSeconds,
            Notes = note,
            Unscheduled = unscheduled,
        };
        A.Log.Append(block);
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
