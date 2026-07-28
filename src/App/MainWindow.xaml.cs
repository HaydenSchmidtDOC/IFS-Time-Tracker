using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Windows.Interop;
using TimeTracker.App.Interop;
using TimeTracker.App.UI;
using TimeTracker.Core;

namespace TimeTracker.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ProjectRow> _rows = new();
    private static readonly Brush GreenBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x9E, 0x6B));

    private static App A => App.Current;

    // Only runs while tracking AND the milliseconds setting is on — the shared 1s Tick isn't
    // fast enough to look like a running stopwatch, but ticking this fast unconditionally would
    // burn cycles for the common case where nobody wants sub-second display.
    private DispatcherTimer? _msTimer;

    public MainWindow()
    {
        InitializeComponent();
        ProjectList.ItemsSource = _rows;

        SourceInitialized += (_, _) => TaskbarMinimizeFix.Apply(new WindowInteropHelper(this).Handle);

        Rebuild();
        UpdateLive();
        SyncMsTimer();
        SyncCloseButtonVisibility();

        A.Tick += OnTick;
        A.StateChanged += OnStateChanged;
    }

    private void OnTick() => UpdateLive();
    private void OnStateChanged() { Rebuild(); UpdateLive(); SyncMsTimer(); SyncCloseButtonVisibility(); }

    /// <summary>Hides the ✕ button per Settings.MinimizeAppOnly — see its own remarks for why.
    /// Minimize is always left in place, so there's still a one-click way to get the window out
    /// of the way that isn't this.</summary>
    private void SyncCloseButtonVisibility()
        => CloseBtn.Visibility = A.Settings.MinimizeAppOnly ? Visibility.Collapsed : Visibility.Visible;

    private void SyncMsTimer()
    {
        bool shouldRun = A.Tracker.IsRunning && A.Settings.ShowTimerMilliseconds;
        if (shouldRun && _msTimer is null)
        {
            _msTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _msTimer.Tick += (_, _) => { if (A.Tracker.IsRunning) TimerText.Text = FormatTimer(A.Tracker.CurrentElapsed); };
            _msTimer.Start();
        }
        else if (!shouldRun && _msTimer is not null)
        {
            _msTimer.Stop();
            _msTimer = null;
        }
    }

    private static string FormatTimer(TimeSpan elapsed) =>
        elapsed.ToString(A.Settings.ShowTimerMilliseconds ? @"hh\:mm\:ss\.ff" : @"hh\:mm\:ss");

    /// <summary>Sync the row list to the current projects, preserving selection by id. When
    /// only the display order changed (pure reorder from settings drag), items are moved rather
    /// than cleared and re-added so WPF keeps their containers alive and can animate them.</summary>
    private void Rebuild()
    {
        var selectedId = (ProjectList.SelectedItem as ProjectRow)?.Id;
        var newProjects = A.Tracker.Projects.Where(p => p.Enabled).ToList();
        var currentIds = _rows.Select(r => r.Id).ToList();
        var newIds = newProjects.Select(p => p.Id).ToList();

        // Pure reorder: same set of IDs, different sequence — animate the moves.
        bool sameSet = currentIds.Count == newIds.Count && currentIds.ToHashSet().SetEquals(newIds);
        if (sameSet && !currentIds.SequenceEqual(newIds))
        {
            AnimateReorder(newIds, selectedId);
            return;
        }

        // A single project being enabled/disabled (or added/deleted outright) is the common
        // case — collapse/expand that one row instead of a hard cut. Anything messier (first
        // load, or several projects changing at once) falls through to the plain rebuild below.
        var removedIds = currentIds.Except(newIds).ToList();
        var addedIds   = newIds.Except(currentIds).ToList();
        if (currentIds.Count > 0 && removedIds.Count == 1 && addedIds.Count == 0)
        {
            AnimateRemove(removedIds[0]);
            return;
        }
        if (addedIds.Count == 1 && removedIds.Count == 0)
        {
            AnimateAdd(newProjects, addedIds[0], selectedId);
            return;
        }

        // Full rebuild (first load, multi-item change, or a data edit).
        _rows.Clear();
        foreach (var p in newProjects)
            _rows.Add(new ProjectRow(p));

        EmptyHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var restore = _rows.FirstOrDefault(r => r.Id == A.Tracker.Active?.Id)
                      ?? _rows.FirstOrDefault(r => r.Id == selectedId);
        ProjectList.SelectedItem = restore;
    }

    /// <summary>Collapses the row's container (height + opacity to zero) and only then removes
    /// it from _rows, so SizeToContent shrinks the window smoothly frame-by-frame alongside the
    /// animation instead of jumping straight to the final size.</summary>
    private void AnimateRemove(string id)
    {
        var row = _rows.FirstOrDefault(r => r.Id == id);
        if (row is null) return;

        if (ProjectList.ItemContainerGenerator.ContainerFromItem(row) is not ListBoxItem container)
        {
            _rows.Remove(row);
            EmptyHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        double from = container.ActualHeight;
        var duration = TimeSpan.FromMilliseconds(180);

        // Explicit From on both — BeginAnimation falls back to the property's current BASE value
        // as an implicit From when none is given, and Height's base value is NaN ("Auto") until
        // something sets it. That's the exact "DoubleAnimation cannot use default origin value of
        // 'NaN'" crash: setting container.Height beforehand didn't reliably count as that base
        // value in every case, so give the animation its own From and skip the base value entirely.
        var heightAnim = new DoubleAnimation(from, 0, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        heightAnim.Completed += (_, _) =>
        {
            _rows.Remove(row);
            EmptyHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        };
        container.BeginAnimation(FrameworkElement.HeightProperty, heightAnim);
        container.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, duration));
    }

    /// <summary>Inserts the new row at its target index immediately (so selection/layout is
    /// correct right away) then grows its container from zero height/opacity once the layout
    /// pass has resolved its natural size.</summary>
    private void AnimateAdd(List<Project> newProjects, string addedId, string? selectedId)
    {
        var project = newProjects.First(p => p.Id == addedId);
        int index = newProjects.FindIndex(p => p.Id == addedId);
        var row = new ProjectRow(project);
        _rows.Insert(Math.Min(index, _rows.Count), row);
        EmptyHint.Visibility = Visibility.Collapsed;

        var restore = _rows.FirstOrDefault(r => r.Id == A.Tracker.Active?.Id)
                      ?? _rows.FirstOrDefault(r => r.Id == selectedId) ?? row;
        ProjectList.SelectedItem = restore;

        // LayoutUpdated, not a fixed-priority Dispatcher callback — it fires synchronously as
        // part of the very layout pass that generates the new container, before that pass is
        // composited to screen. DispatcherPriority.Loaded runs AFTER Render, so the container was
        // already painted once at its natural height/full opacity before this got a chance to
        // zero it out — that stray frame was the visible "shows up, then disappears to animate
        // back in" flash.
        EventHandler? onLayoutUpdated = null;
        onLayoutUpdated = (_, _) =>
        {
            if (ProjectList.ItemContainerGenerator.ContainerFromItem(row) is not ListBoxItem container) return;
            ProjectList.LayoutUpdated -= onLayoutUpdated;

            double natural = container.ActualHeight;
            container.Height = 0;
            container.Opacity = 0;

            var duration = TimeSpan.FromMilliseconds(180);
            var heightAnim = new DoubleAnimation(0, natural, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            // Release back to Auto once grown, so later layout passes aren't pinned to this value.
            heightAnim.Completed += (_, _) => container.ClearValue(FrameworkElement.HeightProperty);
            container.BeginAnimation(FrameworkElement.HeightProperty, heightAnim);
            container.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration));
        };
        ProjectList.LayoutUpdated += onLayoutUpdated;
    }

    /// <summary>Applies a reorder by calling ObservableCollection.Move() (preserves containers,
    /// no flicker) then after the layout pass animates each container from its old screen Y to
    /// its new one via a TranslateTransform slide.</summary>
    private void AnimateReorder(List<string> newIds, string? selectedId)
    {
        // Snapshot current Y positions before any moves.
        var positions = new Dictionary<string, double>();
        for (int i = 0; i < _rows.Count; i++)
        {
            if (ProjectList.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement el)
                positions[_rows[i].Id] = el.TransformToAncestor(ProjectList).Transform(new Point(0, 0)).Y;
        }

        // Apply the new order using Move() so containers stay alive.
        for (int target = 0; target < newIds.Count; target++)
        {
            for (int current = target; current < _rows.Count; current++)
            {
                if (_rows[current].Id == newIds[target])
                {
                    if (current != target) _rows.Move(current, target);
                    break;
                }
            }
        }

        // After the layout pass resolves the new positions, slide each container from
        // where it was to where it now is.
        Dispatcher.InvokeAsync(() =>
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                if (ProjectList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement el) continue;
                if (!positions.TryGetValue(_rows[i].Id, out var oldY)) continue;

                double newY = el.TransformToAncestor(ProjectList).Transform(new Point(0, 0)).Y;
                double deltaY = oldY - newY;
                if (Math.Abs(deltaY) < 0.5) continue;

                var tt = new TranslateTransform(0, deltaY);
                el.RenderTransform = tt;
                tt.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(deltaY, 0, TimeSpan.FromMilliseconds(220))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    });
            }
        }, DispatcherPriority.Loaded);

        var restore = _rows.FirstOrDefault(r => r.Id == A.Tracker.Active?.Id)
                      ?? _rows.FirstOrDefault(r => r.Id == selectedId);
        ProjectList.SelectedItem = restore;
    }

    /// <summary>Repaint the live card, timer, today totals, and the primary button.</summary>
    private void UpdateLive()
    {
        var tracker = A.Tracker;
        bool running = tracker.IsRunning;
        var active = tracker.Active;
        var sel = Selected;

        // live card
        if (running && active is not null)
        {
            LiveDot.Fill = ColorUtil.Brush(active.Color);
            LiveDot.Opacity = 1.0;
            LiveCode.Text = active.Code;
            LiveAsn.Text = $"ASN {active.Asn}" + (string.IsNullOrWhiteSpace(active.Name) ? "" : $" · {active.Name}");
            RecBadge.Visibility = Visibility.Visible;
            TimerText.Text = FormatTimer(tracker.CurrentElapsed);
        }
        else if (sel is not null)
        {
            // Nothing running yet, but a project is selected — preview it here instead of a
            // generic placeholder, so it's clear what pressing start would begin. Dimmed dot +
            // no REC badge keeps it visually distinct from an actually-running project.
            LiveDot.Fill = ColorUtil.Brush(sel.Project.Color);
            LiveDot.Opacity = 0.5;
            LiveCode.Text = sel.Code;
            LiveAsn.Text = $"ASN {sel.Project.Asn}" + (string.IsNullOrWhiteSpace(sel.Project.Name) ? "" : $" · {sel.Project.Name}");
            RecBadge.Visibility = Visibility.Collapsed;
            TimerText.Text = FormatTimer(TimeSpan.Zero);
        }
        else
        {
            LiveDot.Fill = (Brush)FindResource("TextFaint");
            LiveDot.Opacity = 1.0;
            LiveCode.Text = "Not tracking";
            LiveAsn.Text = tracker.Projects.Count == 0 ? "Add a project to begin" : "Select a project, then press start";
            RecBadge.Visibility = Visibility.Collapsed;
            TimerText.Text = FormatTimer(TimeSpan.Zero);
        }

        // today totals per row + grand total
        var totals = tracker.TodaySecondsByProject();
        long grand = 0;
        foreach (var row in _rows)
        {
            totals.TryGetValue(row.Code, out var secs);
            grand += secs;
            row.HoursText = $"{Rounding.ToTenthHours(secs):0.0} h";
            row.LiveVisibility = (running && active?.Id == row.Id) ? Visibility.Visible : Visibility.Collapsed;
        }
        TodayTotal.Text = $"{Rounding.ToTenthHours(grand):0.0} h";

        UpdatePrimary();
    }

    private ProjectRow? Selected => ProjectList.SelectedItem as ProjectRow;

    /// <summary>The round button is context-aware: Start / Stop / Switch.</summary>
    private void UpdatePrimary()
    {
        var tracker = A.Tracker;
        bool running = tracker.IsRunning;
        var sel = Selected;

        if (sel is null)
        {
            PrimaryBtn.IsEnabled = false;
            PrimaryBtn.Content = "▶";
            PrimaryBtn.Background = GreenBrush;
            HintText.Text = "Add a project to begin.";
            return;
        }

        PrimaryBtn.IsEnabled = true;
        if (!running)
        {
            PrimaryBtn.Content = "▶";
            PrimaryBtn.Background = GreenBrush;
            PrimaryBtn.ToolTip = $"Start {sel.Code}";
            HintText.Text = "Double-click a project, or select it and press start.";
        }
        else if (sel.Id == tracker.Active?.Id)
        {
            PrimaryBtn.Content = "■";
            // SetResourceReference (not a one-off FindResource snapshot) so this tracks accent
            // changes live — FindResource captured the brush once and only refreshed the next
            // time this method happened to run, which read as a lag after changing the accent.
            PrimaryBtn.SetResourceReference(Button.BackgroundProperty, "Live");
            PrimaryBtn.ToolTip = "Stop";
            HintText.Text = $"Tracking {sel.Code}. Select another to switch.";
        }
        else
        {
            PrimaryBtn.Content = "⇆";
            PrimaryBtn.SetResourceReference(Button.BackgroundProperty, "Accent");
            PrimaryBtn.ToolTip = $"Switch to {sel.Code}";
            HintText.Text = $"Switch to {sel.Code} — banks the current block first.";
        }
    }

    private void Primary_Click(object sender, RoutedEventArgs e) => ToggleSelected();

    /// <summary>Start/switch to the selected project, or — if it's already the live one — stop
    /// it instead. Shared by the round button, double-click, and Enter, so all three behave the
    /// same way: double-click/Enter used to always (re)start regardless, which meant double-
    /// clicking the already-running project silently did nothing (TrackerService.StartOrSwitch's
    /// own "already live" guard), instead of stopping it the way the button does.</summary>
    private void ToggleSelected()
    {
        var tracker = A.Tracker;
        var sel = Selected;
        if (sel is null) return;

        if (tracker.IsRunning && sel.Id == tracker.Active?.Id) A.StopWithPrompt();
        else A.StartOrSwitch(sel.Project);
    }

    private void ProjectList_SelectionChanged(object sender, SelectionChangedEventArgs e) { UpdatePrimary(); UpdateLive(); }
    private void ProjectList_DoubleClick(object sender, MouseButtonEventArgs e) => ToggleSelected();
    private void ProjectList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ToggleSelected(); e.Handled = true; }
    }

    private void AddProject_Click(object sender, RoutedEventArgs e)
    {
        var p = AddProjectDialog.Ask(this, A.Tracker.Projects);
        if (p is not null) { A.Tracker.AddProject(p); }
    }

    private SettingsWindow? _settings;

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is { IsVisible: true })
        {
            _settings.Close();
            return;
        }
        _settings = new SettingsWindow { Owner = this };
        _settings.Closed += (_, _) => { OnStateChanged(); _settings = null; };
        _settings.Show();
    }

    private void Timesheets_Click(object sender, RoutedEventArgs e) => A.OpenTimesheets();

    // ---- custom chrome ----
    private void TitleBar_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void HideToTray_Click(object sender, RoutedEventArgs e) => Hide();

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing minimises to the tray unless the app is really quitting.
        if (!A.IsQuitting)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        A.Tick -= OnTick;
        A.StateChanged -= OnStateChanged;
        _msTimer?.Stop();
        base.OnClosing(e);
    }
}

/// <summary>Row view-model for the Today list.</summary>
public sealed class ProjectRow : INotifyPropertyChanged
{
    public Project Project { get; }
    public string Id => Project.Id;
    public string Code => Project.Code;

    /// <summary>" - {Asn}" when there's an ASN to show and the setting is on, else empty —
    /// computed once at construction since a row is rebuilt whenever anything it depends on
    /// (project data or this setting) changes.</summary>
    public string AsnDisplay => !string.IsNullOrEmpty(Project.Asn) && App.Current.Settings.ShowAsnInMainList
        ? $" - {Project.Asn}" : "";

    public Brush Swatch { get; }

    private string _hoursText = "0.0 h";
    public string HoursText { get => _hoursText; set { _hoursText = value; Notify(nameof(HoursText)); } }

    private Visibility _liveVisibility = Visibility.Collapsed;
    public Visibility LiveVisibility { get => _liveVisibility; set { _liveVisibility = value; Notify(nameof(LiveVisibility)); } }

    public ProjectRow(Project p)
    {
        Project = p;
        Swatch = ColorUtil.Brush(p.Color);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
