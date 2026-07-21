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

        _projects = A.Tracker.Projects.Select(Clone).ToList();
        _mapping = s.IfsExportMapping.Select(m => new MappingEntry { Header = m.Header, Field = m.Field }).ToList();

        RebuildProjectRows();
        RebuildMappingRows();
        RefreshMonthLabel();
    }

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
        A.Store.SaveSettings(s);
        A.RefreshIdleThreshold();

        A.Tracker.UpdateProjects(_projects);
        A.SetPillVisible(ShowPillCheck.IsChecked == true);
        A.SetTrayIconVisible(ShowTrayCheck.IsChecked == true);
        StartupRegistration.SetEnabled(StartWithWindowsCheck.IsChecked == true);
    }

    private void Save_Click(object sender, RoutedEventArgs e) { SaveToSettings(); Close(); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void TitleBar_Drag(object sender, MouseButtonEventArgs e) { if (e.ChangedButton == MouseButton.Left) DragMove(); }
}
