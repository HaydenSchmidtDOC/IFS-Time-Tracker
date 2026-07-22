using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TimeTracker.App.UI;
using TimeTracker.Core;

namespace TimeTracker.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ProjectRow> _rows = new();
    private static readonly Brush GreenBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x9E, 0x6B));

    private static App A => App.Current;

    public MainWindow()
    {
        InitializeComponent();
        ProjectList.ItemsSource = _rows;

        Rebuild();
        UpdateLive();

        A.Tick += OnTick;
        A.StateChanged += OnStateChanged;
    }

    private void OnTick() => UpdateLive();
    private void OnStateChanged() { Rebuild(); UpdateLive(); }

    /// <summary>Sync the row list to the current projects, preserving selection by code.</summary>
    private void Rebuild()
    {
        var selectedCode = (ProjectList.SelectedItem as ProjectRow)?.Code;
        _rows.Clear();
        foreach (var p in A.Tracker.Projects)
            _rows.Add(new ProjectRow(p));

        EmptyHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Whatever is actually live takes priority — e.g. switching projects via the quick
        // switcher or the pill should be reflected here too, not leave the old selection
        // sitting stale. Only fall back to the prior selection (or none at all) once nothing
        // is running; a fresh boot with nothing active shouldn't auto-pick an arbitrary
        // project just to have something selected.
        var restore = _rows.FirstOrDefault(r => r.Code == A.Tracker.Active?.Code)
                      ?? _rows.FirstOrDefault(r => r.Code == selectedCode);
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
            TimerText.Text = App.FormatElapsed(tracker.CurrentElapsedSeconds);
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
            TimerText.Text = "00:00:00";
        }
        else
        {
            LiveDot.Fill = (Brush)FindResource("TextFaint");
            LiveDot.Opacity = 1.0;
            LiveCode.Text = "Not tracking";
            LiveAsn.Text = tracker.Projects.Count == 0 ? "Add a project to begin" : "Select a project, then press start";
            RecBadge.Visibility = Visibility.Collapsed;
            TimerText.Text = "00:00:00";
        }

        // today totals per row + grand total
        var totals = tracker.TodaySecondsByProject();
        long grand = 0;
        foreach (var row in _rows)
        {
            totals.TryGetValue(row.Code, out var secs);
            grand += secs;
            row.HoursText = $"{Rounding.ToTenthHours(secs):0.0} h";
            row.LiveVisibility = (running && active?.Code == row.Code) ? Visibility.Visible : Visibility.Collapsed;
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
        else if (sel.Code == tracker.Active?.Code)
        {
            PrimaryBtn.Content = "■";
            PrimaryBtn.Background = (Brush)FindResource("Live");
            PrimaryBtn.ToolTip = "Stop";
            HintText.Text = $"Tracking {sel.Code}. Select another to switch.";
        }
        else
        {
            PrimaryBtn.Content = "⇆";
            PrimaryBtn.Background = (Brush)FindResource("Accent");
            PrimaryBtn.ToolTip = $"Switch to {sel.Code}";
            HintText.Text = $"Switch to {sel.Code} — banks the current block first.";
        }
    }

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        var tracker = A.Tracker;
        var sel = Selected;
        if (sel is null) return;

        if (tracker.IsRunning && sel.Code == tracker.Active?.Code) A.StopWithPrompt();
        else A.StartOrSwitch(sel.Project);
    }

    private void StartSelected()
    {
        if (Selected is { } sel) A.StartOrSwitch(sel.Project);
    }

    private void ProjectList_SelectionChanged(object sender, SelectionChangedEventArgs e) { UpdatePrimary(); UpdateLive(); }
    private void ProjectList_DoubleClick(object sender, MouseButtonEventArgs e) => StartSelected();
    private void ProjectList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { StartSelected(); e.Handled = true; }
    }

    private void AddProject_Click(object sender, RoutedEventArgs e)
    {
        var p = AddProjectDialog.Ask(this, A.Tracker.Projects);
        if (p is not null) { A.Tracker.AddProject(p); }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        new SettingsWindow { Owner = this }.ShowDialog();
        OnStateChanged();
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
        base.OnClosing(e);
    }
}

/// <summary>Row view-model for the Today list.</summary>
public sealed class ProjectRow : INotifyPropertyChanged
{
    public Project Project { get; }
    public string Code => Project.Code;
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
