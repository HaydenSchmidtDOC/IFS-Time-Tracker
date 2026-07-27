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

    // Fixed saturation/lightness for the custom accent colour — same pair AddProjectDialog uses
    // for project colours, so both hue sliders pick from a visually matching rainbow.
    private const double AccentSaturation = 0.55;
    private const double AccentLightness = 0.50;

    private static readonly string[] ThemeModes = { "Light", "Dark", "System", "Custom" };

    private readonly List<Project> _projects;                 // working copy
    private readonly List<MappingEntry> _mapping;              // working copy
    private DateTime _exportMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private bool _closingConfirmed;                            // set once Save/Discard has resolved, so OnClosing doesn't re-prompt

    private SegmentedToggle _themeModeToggle = null!;
    private SegmentedToggle _accentModeToggle = null!;

    private ToggleSwitch _promptNoteToggle = null!;
    private ToggleSwitch _showPillToggle = null!;
    private ToggleSwitch _showTrayToggle = null!;
    private ToggleSwitch _startMinimizedToggle = null!;
    private ToggleSwitch _startWithWindowsToggle = null!;

    public SettingsWindow()
    {
        InitializeComponent();

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null ? "" : $"IFS Time Tracker v{version.Major}.{version.Minor}.{version.Build}";

        var s = A.Settings;
        _promptNoteToggle = new ToggleSwitch(s.PromptForNote);
        PromptNoteToggleHost.Content = _promptNoteToggle.Root;
        _showPillToggle = new ToggleSwitch(s.PillVisible);
        ShowPillToggleHost.Content = _showPillToggle.Root;
        _showTrayToggle = new ToggleSwitch(s.ShowTrayIcon);
        ShowTrayToggleHost.Content = _showTrayToggle.Root;
        _startMinimizedToggle = new ToggleSwitch(s.StartMinimized);
        StartMinimizedToggleHost.Content = _startMinimizedToggle.Root;
        _startWithWindowsToggle = new ToggleSwitch(StartupRegistration.IsEnabled());
        StartWithWindowsToggleHost.Content = _startWithWindowsToggle.Root;
        IdleBox.Text = s.IdleThresholdMinutes.ToString();
        DateFormatBox.Text = s.ExportDateFormat;
        DataFolderText.Text = A.Paths.DataFolder;
        DataOverrideBox.Text = s.DataFolderOverride ?? "";

        _projects = A.Tracker.Projects.Select(Clone).ToList();
        _mapping = s.IfsExportMapping.Select(m => new MappingEntry { Header = m.Header, Field = m.Field }).ToList();

        InitAppearance(s);

        RebuildProjectRows();
        RebuildMappingRows();
        RefreshMonthLabel();
    }

    // ================= Appearance =================
    //
    // Every control here applies immediately — write straight to Settings, save, re-skin — the
    // same convention TimesheetWindow's own settings popover uses (see its InitTimesheetSettings-
    // Popover remarks) rather than buffering into a working copy for the Save button below.
    // Appearance is something you want to see change the instant you touch the control (that's
    // the whole point of a live preview while panning through themes), and there's no sensible
    // "cancel" story for "what does this look like" the way there is for editing a project.

    private void InitAppearance(Settings s)
    {
        int themeModeIndex = Array.IndexOf(ThemeModes, s.ThemeMode);
        if (themeModeIndex < 0) themeModeIndex = 2; // "System" — unrecognised/legacy value
        _themeModeToggle = new SegmentedToggle(ThemeModes, themeModeIndex, fontSize: 11.5);
        _themeModeToggle.SelectionChanged += index =>
        {
            A.Settings.ThemeMode = ThemeModes[index];
            ApplyAppearanceChange();
        };
        ThemeModeHost.Content = _themeModeToggle.Root;

        int accentModeIndex = s.AccentMode == "Custom" ? 1 : 0;
        _accentModeToggle = new SegmentedToggle(new[] { "System default", "Custom" }, accentModeIndex, fontSize: 11.5);
        _accentModeToggle.SelectionChanged += index =>
        {
            A.Settings.AccentMode = index == 1 ? "Custom" : "System";
            ApplyAppearanceChange();
        };
        AccentModeHost.Content = _accentModeToggle.Root;

        string accentColor = string.IsNullOrWhiteSpace(s.CustomAccentColor) ? "#2D6B8F" : s.CustomAccentColor;
        AccentHueSlider.Value = ColorUtil.GetHue(ColorUtil.Parse(accentColor));
        AccentPreviewBrush.Color = ColorUtil.Parse(accentColor);
        AccentHueSlider.ValueChanged += (_, e) =>
        {
            A.Settings.CustomAccentColor = ColorUtil.ToHex(ColorUtil.FromHsl(e.NewValue, AccentSaturation, AccentLightness));
            AccentPreviewBrush.Color = ColorUtil.Parse(A.Settings.CustomAccentColor);
            ApplyAppearanceChange(refreshVisibility: false); // dragging the hue never changes which panels show
        };

        foreach (ComboBoxItem item in CustomThemeCombo.Items)
        {
            if (Equals(item.Content, s.CustomThemeName)) { CustomThemeCombo.SelectedItem = item; break; }
        }
        CustomThemeCombo.SelectedItem ??= CustomThemeCombo.Items[0];
        CustomThemeCombo.SelectionChanged += (_, _) =>
        {
            if (CustomThemeCombo.SelectedItem is ComboBoxItem { Content: string name })
                A.Settings.CustomThemeName = name;
            ApplyAppearanceChange();
        };

        RefreshAppearanceVisibility();
    }

    /// <summary>Persists the appearance setting just changed and re-skins the app right now.</summary>
    private void ApplyAppearanceChange(bool refreshVisibility = true)
    {
        A.Store.SaveSettings(A.Settings);
        A.ApplyThemeAndAccent();
        if (refreshVisibility) RefreshAppearanceVisibility();
    }

    /// <summary>Custom-theme presets bundle their own accent colour (confirmed with the user —
    /// no override on top), so the accent picker only makes sense for Light/Dark/System.</summary>
    private void RefreshAppearanceVisibility()
    {
        bool isCustomTheme = _themeModeToggle.SelectedIndex == 3;
        AccentPanel.Visibility = isCustomTheme ? Visibility.Collapsed : Visibility.Visible;
        CustomThemePanel.Visibility = isCustomTheme ? Visibility.Visible : Visibility.Collapsed;
        AccentSliderRow.Visibility = !isCustomTheme && _accentModeToggle.SelectedIndex == 1
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Project Clone(Project p) => new() { Id = p.Id, Code = p.Code, Asn = p.Asn, Name = p.Name, Color = p.Color, Order = p.Order };

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
            // Exclude by Id, not Code — Code is now editable (see AddProjectDialog), so the
            // uniqueness check below needs a stable way to mean "every OTHER project".
            var updated = AddProjectDialog.Edit(this, p, _projects.Where(x => x.Id != p.Id).ToList());
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
        s.PromptForNote = _promptNoteToggle.IsOn;
        s.StartMinimized = _startMinimizedToggle.IsOn;
        s.DataFolderOverride = string.IsNullOrWhiteSpace(DataOverrideBox.Text) ? null : DataOverrideBox.Text.Trim();
        if (int.TryParse(IdleBox.Text, out var mins) && mins > 0) s.IdleThresholdMinutes = mins;
        s.ExportDateFormat = string.IsNullOrWhiteSpace(DateFormatBox.Text) ? s.ExportDateFormat : DateFormatBox.Text;
        s.IfsExportMapping = _mapping.Where(m => !string.IsNullOrWhiteSpace(m.Header)).ToList();
        A.Store.SaveSettings(s);
        A.RefreshIdleThreshold();

        A.Tracker.UpdateProjects(_projects);
        A.SetPillVisible(_showPillToggle.IsOn);
        A.SetTrayIconVisible(_showTrayToggle.IsOn);
        StartupRegistration.SetEnabled(_startWithWindowsToggle.IsOn);
    }

    private void Save_Click(object sender, RoutedEventArgs e) { _closingConfirmed = true; SaveToSettings(); Close(); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // Goes through the same Close() -> OnClosing unsaved-changes guard as the ✕ button (rather
    // than forcing _closingConfirmed = true) — starting the tour isn't an explicit "discard my
    // edits" action, so any pending changes still get the normal save/discard/cancel prompt.
    // Close() is synchronous: if OnClosing cancels it, the window is still IsVisible when it
    // returns, so that's the signal the tour should NOT start after all.
    private void StartTutorial_Click(object sender, RoutedEventArgs e)
    {
        Close();
        if (!IsVisible) A.StartTutorial();
    }
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
        if (_promptNoteToggle.IsOn != s.PromptForNote) return true;
        if (_showPillToggle.IsOn != s.PillVisible) return true;
        if (_showTrayToggle.IsOn != s.ShowTrayIcon) return true;
        if (_startMinimizedToggle.IsOn != s.StartMinimized) return true;
        if (_startWithWindowsToggle.IsOn != StartupRegistration.IsEnabled()) return true;
        if (int.TryParse(IdleBox.Text, out var mins) && mins > 0 && mins != s.IdleThresholdMinutes) return true;
        if (DateFormatBox.Text != s.ExportDateFormat) return true;

        var overrideText = string.IsNullOrWhiteSpace(DataOverrideBox.Text) ? null : DataOverrideBox.Text.Trim();
        if (overrideText != s.DataFolderOverride) return true;

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
