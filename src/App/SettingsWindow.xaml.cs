using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using TimeTracker.App.UI;
using TimeTracker.Core;

namespace TimeTracker.App;

public partial class SettingsWindow : Window
{
    private static App A => App.Current;

    private readonly List<Project> _projects;                 // working copy
    private readonly List<MappingEntry> _mapping;              // working copy
    private DateTime _exportMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private double _chartMinSegmentHours;
    private bool _chartMergeAllSessions;
    private bool _closingConfirmed;                            // set once Save/Discard has resolved, so OnClosing doesn't re-prompt

    public SettingsWindow()
    {
        InitializeComponent();

        var s = A.Settings;
        PromptNoteCheck.IsChecked = s.PromptForNote;
        ShowPillCheck.IsChecked = s.PillVisible;
        ShowTrayCheck.IsChecked = s.ShowTrayIcon;
        StartMinimizedCheck.IsChecked = s.StartMinimized;
        StartWithWindowsCheck.IsChecked = StartupRegistration.IsEnabled();
        IdleBox.Text = s.IdleThresholdMinutes.ToString();
        DateFormatBox.Text = s.ExportDateFormat;
        DataFolderText.Text = A.Paths.DataFolder;
        DataOverrideBox.Text = s.DataFolderOverride ?? "";
        _chartMinSegmentHours = s.ChartMinSegmentHours;
        _chartMergeAllSessions = s.ChartMergeAllSessions;
        MergeSessionsCheck.IsChecked = _chartMergeAllSessions;

        _projects = A.Tracker.Projects.Select(Clone).ToList();
        _mapping = s.IfsExportMapping.Select(m => new MappingEntry { Header = m.Header, Field = m.Field }).ToList();

        RebuildProjectRows();
        RebuildMappingRows();
        RefreshMonthLabel();

        // If the saved value is already index 0, setting Value=0 below is a no-op and
        // ValueChanged never fires — so the label text is applied explicitly here too, not
        // left to only happen as a side effect of the event.
        int startIdx = ClosestDensityIndex(_chartMinSegmentHours);
        DensitySlider.Value = startIdx;
        RefreshDensityLabel(startIdx);
    }

    // ================= chart density =================
    // The slider is index-based (0-6), not value-based — the presets aren't evenly spaced
    // (fine-grained near zero, coarser further out), so a real-valued slider couldn't snap
    // evenly across them.

    private static readonly double[] DensityPresets = { 0, 0.05, 0.1, 0.25, 0.5, 1, 2 };

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
        _chartMinSegmentHours = DensityPresets[idx];
        RefreshDensityLabel(idx);
    }

    private void RefreshDensityLabel(int idx)
        => DensityValueText.Text = DensityPresets[idx] <= 0 ? "Off" : $"Fold under {DensityPresets[idx]:0.##} h";

    private void MergeSessionsCheck_Changed(object sender, RoutedEventArgs e)
        => _chartMergeAllSessions = MergeSessionsCheck.IsChecked == true;

    private static Project Clone(Project p) => new() { Code = p.Code, Asn = p.Asn, Name = p.Name, Color = p.Color, Order = p.Order };

    // ================= Projects =================

    private void RebuildProjectRows()
    {
        ProjectsList.Items.Clear();
        foreach (var p in _projects)
            ProjectsList.Items.Add(BuildProjectRow(p));

        if (_projects.Count == 0)
        {
            ProjectsList.Items.Add(new TextBlock
            {
                Text = "No projects yet.", FontSize = 12,
                Foreground = (Brush)FindResource("TextFaint"), Margin = new Thickness(2, 4, 0, 4)
            });
        }
    }

    private UIElement BuildProjectRow(Project p)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new Rectangle { Width = 12, Height = 12, RadiusX = 3, RadiusY = 3, Fill = ColorUtil.Brush(p.Color), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(dot, 0);

        var label = new TextBlock
        {
            Text = $"{p.Code}   ASN {p.Asn}", FontSize = 13, Margin = new Thickness(10, 0, 0, 0),
            Foreground = (Brush)FindResource("Text"), VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(label, 1);

        var editBtn = new Button { Content = "Edit", Style = (Style)FindResource("Btn"), FontSize = 11, Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(0, 0, 6, 0) };
        editBtn.Click += (_, _) =>
        {
            var updated = AddProjectDialog.Edit(this, p, _projects.Where(x => x.Code != p.Code).ToList());
            if (updated is not null) RebuildProjectRows();
        };
        Grid.SetColumn(editBtn, 2);

        var delBtn = new Button { Content = "✕", Style = (Style)FindResource("Btn"), FontSize = 11, Padding = new Thickness(8, 3, 8, 3) };
        delBtn.Click += (_, _) => { _projects.Remove(p); RebuildProjectRows(); };
        Grid.SetColumn(delBtn, 3);

        grid.Children.Add(dot);
        grid.Children.Add(label);
        grid.Children.Add(editBtn);
        grid.Children.Add(delBtn);
        return grid;
    }

    private void AddProject_Click(object sender, RoutedEventArgs e)
    {
        var p = AddProjectDialog.Ask(this, _projects);
        if (p is not null) { _projects.Add(p); RebuildProjectRows(); }
    }

    // ================= IFS mapping =================

    private void RebuildMappingRows()
    {
        MappingList.Items.Clear();
        foreach (var m in _mapping)
            MappingList.Items.Add(BuildMappingRow(m));
    }

    private UIElement BuildMappingRow(MappingEntry m)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var headerBox = new TextBox { Text = m.Header, Style = (Style)FindResource("Input"), FontSize = 12 };
        headerBox.TextChanged += (_, _) => m.Header = headerBox.Text;
        Grid.SetColumn(headerBox, 0);

        var arrow = new TextBlock { Text = "←", Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("TextFaint") };
        Grid.SetColumn(arrow, 1);

        var fieldBox = new TextBox { Text = m.Field, Style = (Style)FindResource("Input"), FontSize = 12 };
        fieldBox.TextChanged += (_, _) => m.Field = fieldBox.Text;
        Grid.SetColumn(fieldBox, 2);

        var delBtn = new Button { Content = "✕", Style = (Style)FindResource("Btn"), FontSize = 11, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(6, 0, 0, 0) };
        delBtn.Click += (_, _) => { _mapping.Remove(m); RebuildMappingRows(); };
        Grid.SetColumn(delBtn, 3);

        grid.Children.Add(headerBox);
        grid.Children.Add(arrow);
        grid.Children.Add(fieldBox);
        grid.Children.Add(delBtn);
        return grid;
    }

    private void AddMapping_Click(object sender, RoutedEventArgs e)
    {
        _mapping.Add(new MappingEntry { Header = "Column", Field = "ProjectCode" });
        RebuildMappingRows();
    }

    // ================= export =================

    private void RefreshMonthLabel() => MonthLabel.Text = _exportMonth.ToString("MMMM yyyy");
    private void PrevMonth_Click(object sender, RoutedEventArgs e) { _exportMonth = _exportMonth.AddMonths(-1); RefreshMonthLabel(); }
    private void NextMonth_Click(object sender, RoutedEventArgs e) { _exportMonth = _exportMonth.AddMonths(1); RefreshMonthLabel(); }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        SaveToSettings(); // export uses current mapping/date-format
        var path = A.Exporter.ExportMonth(_exportMonth.Year, _exportMonth.Month, A.Settings);
        ExportResultText.Text = $"Exported to: {path}";
        ExportResultText.Visibility = Visibility.Visible;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(A.Paths.DataFolder) { UseShellExecute = true }); }
        catch { /* best effort */ }
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Choose a folder for IFS Time Tracker data",
            SelectedPath = string.IsNullOrWhiteSpace(DataOverrideBox.Text) ? A.Paths.DataFolder : DataOverrideBox.Text,
            ShowNewFolderButton = true,
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            DataOverrideBox.Text = dlg.SelectedPath;
    }

    // ================= save/close =================

    private void SaveToSettings()
    {
        var s = A.Settings;
        s.PromptForNote = PromptNoteCheck.IsChecked == true;
        s.StartMinimized = StartMinimizedCheck.IsChecked == true;
        s.DataFolderOverride = string.IsNullOrWhiteSpace(DataOverrideBox.Text) ? null : DataOverrideBox.Text.Trim();
        if (int.TryParse(IdleBox.Text, out var mins) && mins > 0) s.IdleThresholdMinutes = mins;
        s.ExportDateFormat = string.IsNullOrWhiteSpace(DateFormatBox.Text) ? s.ExportDateFormat : DateFormatBox.Text;
        s.IfsExportMapping = _mapping.Where(m => !string.IsNullOrWhiteSpace(m.Header)).ToList();
        s.ChartMinSegmentHours = _chartMinSegmentHours;
        s.ChartMergeAllSessions = _chartMergeAllSessions;
        A.Store.SaveSettings(s);
        A.RefreshIdleThreshold();

        A.Tracker.UpdateProjects(_projects);
        A.SetPillVisible(ShowPillCheck.IsChecked == true);
        A.SetTrayIconVisible(ShowTrayCheck.IsChecked == true);
        StartupRegistration.SetEnabled(StartWithWindowsCheck.IsChecked == true);
    }

    private void Save_Click(object sender, RoutedEventArgs e) { _closingConfirmed = true; SaveToSettings(); Close(); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void TitleBar_Drag(object sender, MouseButtonEventArgs e) { if (e.ChangedButton == MouseButton.Left) DragMove(); }

    // ================= unsaved-changes guard =================

    protected override void OnClosing(CancelEventArgs e)
    {
        // Resolve the outcome and set e.Cancel (at most) once for THIS close pass, rather than
        // cancelling it and then calling Close() again from inside the handler — a recursive
        // Close() while still inside this window's own Closing callback re-enters WPF's closing
        // machinery on the same window and crashed instead of throwing anything catchable.
        if (!_closingConfirmed && HasUnsavedChanges())
        {
            switch (UnsavedChangesPrompt.Ask(this))
            {
                case UnsavedChangesResult.Save:
                    SaveToSettings();
                    _closingConfirmed = true;
                    break;
                case UnsavedChangesResult.Discard:
                    _closingConfirmed = true;
                    break;
                case UnsavedChangesResult.Cancel:
                default:
                    e.Cancel = true;
                    break;
            }
        }

        base.OnClosing(e);
    }

    private bool HasUnsavedChanges()
    {
        var s = A.Settings;
        if (PromptNoteCheck.IsChecked == true != s.PromptForNote) return true;
        if (ShowPillCheck.IsChecked == true != s.PillVisible) return true;
        if (ShowTrayCheck.IsChecked == true != s.ShowTrayIcon) return true;
        if (StartMinimizedCheck.IsChecked == true != s.StartMinimized) return true;
        if (StartWithWindowsCheck.IsChecked == true != StartupRegistration.IsEnabled()) return true;
        if (int.TryParse(IdleBox.Text, out var mins) && mins > 0 && mins != s.IdleThresholdMinutes) return true;
        if (DateFormatBox.Text != s.ExportDateFormat) return true;

        var overrideText = string.IsNullOrWhiteSpace(DataOverrideBox.Text) ? null : DataOverrideBox.Text.Trim();
        if (overrideText != s.DataFolderOverride) return true;

        if (_chartMinSegmentHours != s.ChartMinSegmentHours) return true;
        if (_chartMergeAllSessions != s.ChartMergeAllSessions) return true;
        if (!ProjectsEqual(_projects, A.Tracker.Projects)) return true;
        if (!MappingEqual(_mapping, s.IfsExportMapping)) return true;

        return false;
    }

    private static bool ProjectsEqual(IReadOnlyList<Project> a, IReadOnlyList<Project> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            var x = a[i]; var y = b[i];
            if (x.Code != y.Code || x.Asn != y.Asn || x.Name != y.Name || x.Color != y.Color || x.Order != y.Order)
                return false;
        }
        return true;
    }

    private static bool MappingEqual(IReadOnlyList<MappingEntry> a, IReadOnlyList<MappingEntry> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i].Header != b[i].Header || a[i].Field != b[i].Field) return false;
        return true;
    }
}
