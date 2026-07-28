using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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

    private readonly List<Project> _projects;
    private List<FrameworkElement> _rowElements = new();

    // drag state — all reset when not dragging
    private int    _dragFromIndex    = -1;
    private int    _dragToIndex      = -1;
    private double _dragCursorOffsetY;
    private double _dragRowHeight;
    private double[] _naturalRowTops  = [];
    private Popup?   _dragGhost;

    private SegmentedToggle _themeModeToggle = null!;
    private SegmentedToggle _accentModeToggle = null!;

    private ToggleSwitch _promptNoteToggle = null!;
    private ToggleSwitch _showPillToggle = null!;
    private ToggleSwitch _showTrayToggle = null!;
    private ToggleSwitch _startMinimizedToggle = null!;
    private ToggleSwitch _startWithWindowsToggle = null!;
    private ToggleSwitch _minimizeAppOnlyToggle = null!;
    private ToggleSwitch _showAsnToggle = null!;
    private ToggleSwitch _showMsToggle = null!;

    public SettingsWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => PositionNextToOwner();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null ? "" : $"IFS Time Tracker v{version.Major}.{version.Minor}.{version.Build}";

        var s = A.Settings;
        _promptNoteToggle = new ToggleSwitch(s.PromptForNote);
        _promptNoteToggle.Toggled += on => { A.Settings.PromptForNote = on; A.Store.SaveSettings(A.Settings); };
        PromptNoteToggleHost.Content = _promptNoteToggle.Root;
        _showMsToggle = new ToggleSwitch(s.ShowTimerMilliseconds);
        _showMsToggle.Toggled += on => A.SetShowTimerMilliseconds(on);
        ShowMsToggleHost.Content = _showMsToggle.Root;
        _showPillToggle = new ToggleSwitch(s.PillVisible);
        _showPillToggle.Toggled += on => A.SetPillVisible(on);
        ShowPillToggleHost.Content = _showPillToggle.Root;
        _showTrayToggle = new ToggleSwitch(s.ShowTrayIcon);
        _showTrayToggle.Toggled += on => A.SetTrayIconVisible(on);
        ShowTrayToggleHost.Content = _showTrayToggle.Root;
        _startMinimizedToggle = new ToggleSwitch(s.StartMinimized);
        _startMinimizedToggle.Toggled += on => { A.Settings.StartMinimized = on; A.Store.SaveSettings(A.Settings); };
        StartMinimizedToggleHost.Content = _startMinimizedToggle.Root;
        _startWithWindowsToggle = new ToggleSwitch(StartupRegistration.IsEnabled());
        _startWithWindowsToggle.Toggled += on => StartupRegistration.SetEnabled(on);
        StartWithWindowsToggleHost.Content = _startWithWindowsToggle.Root;
        _minimizeAppOnlyToggle = new ToggleSwitch(s.MinimizeAppOnly);
        _minimizeAppOnlyToggle.Toggled += on => A.SetMinimizeAppOnly(on);
        MinimizeAppOnlyToggleHost.Content = _minimizeAppOnlyToggle.Root;
        _showAsnToggle = new ToggleSwitch(s.ShowAsnInMainList);
        _showAsnToggle.Toggled += on => A.SetShowAsnInMainList(on);
        ShowAsnToggleHost.Content = _showAsnToggle.Root;
        IdleBox.Text = s.IdleThresholdMinutes.ToString();
        IdleBox.LostFocus += (_, _) =>
        {
            if (int.TryParse(IdleBox.Text, out var mins) && mins > 0)
            {
                A.Settings.IdleThresholdMinutes = mins;
                A.Store.SaveSettings(A.Settings);
                A.RefreshIdleThreshold();
            }
        };
        DataFolderText.Text = A.Paths.DataFolder;
        DataOverrideBox.Text = s.DataFolderOverride ?? "";
        DataOverrideBox.LostFocus += (_, _) =>
        {
            A.Settings.DataFolderOverride = string.IsNullOrWhiteSpace(DataOverrideBox.Text) ? null : DataOverrideBox.Text.Trim();
            A.Store.SaveSettings(A.Settings);
        };

        _projects = A.Tracker.Projects.Select(Clone).ToList();

        InitAppearance(s);

        RebuildProjectRows();
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

    private static Project Clone(Project p) => new() { Id = p.Id, Code = p.Code, Asn = p.Asn, Name = p.Name, Color = p.Color, Order = p.Order, Enabled = p.Enabled };

    // ================= Projects =================
    // Drag uses mouse capture (not WPF DragDrop) so we can:
    //   • show a floating ghost that follows the cursor
    //   • animate the other rows sliding aside as the ghost crosses their midpoints
    // Items use TranslateTransform for the visual shift; layout positions are unchanged so the
    // StackPanel height stays constant throughout the drag.

    private void RebuildProjectRows()
    {
        CleanUpDrag();
        foreach (var el in _rowElements) { el.Opacity = 1; el.RenderTransform = null; }

        ProjectsList.Items.Clear();
        _rowElements.Clear();

        foreach (var p in _projects)
        {
            var row = (FrameworkElement)BuildProjectRow(p);
            _rowElements.Add(row);
            ProjectsList.Items.Add(row);
        }

        if (_projects.Count == 0)
        {
            ProjectsList.Items.Add(new TextBlock
            {
                Text = "No projects yet.", FontSize = 12,
                Foreground = (Brush)FindResource("TextFaint"), Margin = new Thickness(2, 4, 0, 4)
            });
        }
    }

    // Clicking the swatch toggles Project.Enabled instead of starting a drag — its container is
    // tagged with this sentinel so the row's drag-start handler can exclude it the same way it
    // already excludes Edit/Delete (see IsInsideDragExclusion).
    private static readonly object NoDragZone = new();

    private UIElement BuildProjectRow(Project p)
    {
        // grid.Background is set (even though transparent) because an unset Background isn't
        // hit-testable in WPF — without it, PreviewMouseLeftButtonDown only fires over the dot/
        // text/buttons, not the row's own blank space, so most of the "whole bounding box" would
        // silently not start a drag.
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3), Background = Brushes.Transparent };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                          // 0: swatch
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });     // 1: label
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                          // 2: edit
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                          // 3: delete

        // Hover highlight (not a special cursor) is the row's only "this can be dragged" hint —
        // a plain Border layer behind everything else so it doesn't affect hit-testing/drag-
        // exclusion, spanning all columns so the whole row lights up together.
        var hoverBg = new Border { CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, IsHitTestVisible = false };
        Grid.SetColumnSpan(hoverBg, 4);
        grid.MouseEnter += (_, _) => hoverBg.SetResourceReference(Border.BackgroundProperty, "Surface2");
        grid.MouseLeave += (_, _) => hoverBg.Background = Brushes.Transparent;

        var dot = new Rectangle { Width = 12, Height = 12, RadiusX = 3, RadiusY = 3, VerticalAlignment = VerticalAlignment.Center };
        ApplySwatchAppearance(dot, p);
        var swatchHost = new Border
        {
            Padding = new Thickness(4), Background = Brushes.Transparent, Cursor = Cursors.Hand,
            Tag = NoDragZone, Child = dot,
            ToolTip = p.Enabled ? "Click to disable this project" : "Click to enable this project",
        };
        swatchHost.PreviewMouseLeftButtonDown += (_, e) =>
        {
            p.Enabled = !p.Enabled;
            RebuildProjectRows();
            A.Tracker.UpdateProjects(_projects);
            e.Handled = true;
        };
        Grid.SetColumn(swatchHost, 0);

        var label = new TextBlock
        {
            FontSize = 13, Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            // Grid doesn't clip children to their cell by default — the ghost's copy of this
            // label already needed this (see BuildGhostContent); this row's own always-visible
            // label needed it too, and was still bleeding into the Edit/Delete columns without it.
            ClipToBounds = true,
        };
        label.Inlines.Add(new Run(p.Code) { Foreground = (Brush)FindResource("Text") });
        if (!string.IsNullOrEmpty(p.Asn))
            label.Inlines.Add(new Run($" - {p.Asn}") { Foreground = (Brush)FindResource("TextFaint"), FontSize = 11 });
        Grid.SetColumn(label, 1);

        var editBtn = new Button { Content = "Edit", Style = (Style)FindResource("Btn"), FontSize = 11, Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(0, 0, 6, 0) };
        editBtn.Click += (_, _) =>
        {
            var updated = AddProjectDialog.Edit(this, p, _projects.Where(x => x.Id != p.Id).ToList());
            if (updated is not null) { RebuildProjectRows(); A.Tracker.UpdateProjects(_projects); }
        };
        Grid.SetColumn(editBtn, 2);

        var delBtn = new Button { Content = "✕", Style = (Style)FindResource("Btn"), FontSize = 11, Padding = new Thickness(8, 3, 8, 3) };
        delBtn.Click += (_, _) => { _projects.Remove(p); RebuildProjectRows(); A.Tracker.UpdateProjects(_projects); };
        Grid.SetColumn(delBtn, 3);

        grid.Children.Add(hoverBg);
        grid.Children.Add(swatchHost);
        grid.Children.Add(label);
        grid.Children.Add(editBtn);
        grid.Children.Add(delBtn);

        // Whole row starts a drag except Edit/Delete/the swatch, which still need their own
        // clicks — PreviewMouseLeftButtonDown tunnels through the row before any of their own
        // handling, so it's checked here rather than needing per-control suppression.
        grid.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (IsInsideDragExclusion(e.OriginalSource as DependencyObject)) return;
            BeginDrag(p.Id, e, grid);
        };
        return grid;
    }

    private static void ApplySwatchAppearance(Rectangle swatch, Project p)
    {
        if (p.Enabled)
        {
            swatch.Fill = ColorUtil.Brush(p.Color);
            swatch.Stroke = null;
            swatch.StrokeThickness = 0;
        }
        else
        {
            swatch.Fill = Brushes.Transparent;
            swatch.Stroke = ColorUtil.Brush(p.Color);
            swatch.StrokeThickness = 1.5;
        }
    }

    // A click on the label's text can hand back a Run as OriginalSource — Run is a
    // FrameworkContentElement, not a Visual, and VisualTreeHelper.GetParent throws on anything
    // that isn't a Visual/Visual3D. Fall back to the logical tree for those (a Run's logical
    // parent is its owning TextBlock) so this ever reaches Grid.GetParent instead of crashing.
    private static bool IsInsideDragExclusion(DependencyObject? d)
    {
        while (d is not null)
        {
            if (d is Button) return true;
            if (d is FrameworkElement { Tag: { } tag } && ReferenceEquals(tag, NoDragZone)) return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    // ---- drag lifecycle ----

    private void BeginDrag(string projectId, MouseButtonEventArgs e, FrameworkElement row)
    {
        int from = _projects.FindIndex(x => x.Id == projectId);
        if (from < 0 || _rowElements.Count <= 1) return;

        _dragFromIndex = from;
        _dragToIndex   = from;

        var rowEl  = _rowElements[from];
        rowEl.UpdateLayout();
        _dragRowHeight   = rowEl.ActualHeight + rowEl.Margin.Top + rowEl.Margin.Bottom;
        _dragCursorOffsetY = e.GetPosition(rowEl).Y;

        // snapshot natural tops before any TranslateTransform is applied
        _naturalRowTops = new double[_rowElements.Count];
        for (int i = 0; i < _rowElements.Count; i++)
            _naturalRowTops[i] = _rowElements[i].TransformToAncestor(ProjectsList).Transform(default).Y;

        rowEl.Opacity = 0; // invisible placeholder keeps the layout slot open

        var ghost = BuildGhostContent(from);
        const double shadowPad = 12; // room for drop-shadow bleed on all sides
        // rowEl's own ActualWidth (not ProjectsList's) — the ghost drops the Edit/Delete columns
        // the real row reserves space for, so sizing off ProjectsList left it too narrow for the
        // label to fit its content and it clipped mid-word instead of ellipsizing.
        ghost.Width = rowEl.ActualWidth - shadowPad * 2;
        // No forced Height: the ghost has its own vertical Padding (see BuildGhostContent), and
        // pinning Height to rowEl's (which has none) left too little room inside that padding for
        // the content — combined with ClipToBounds below, that hard-clipped text at the bottom.
        // Auto-sizing to content avoids the mismatch entirely.
        var ghostHost = new Border { Child = ghost, Padding = new Thickness(shadowPad) };
        _dragGhost = new Popup
        {
            AllowsTransparency = true, PopupAnimation = PopupAnimation.None,
            Placement = PlacementMode.Relative, PlacementTarget = ProjectsList, StaysOpen = true,
            Child = ghostHost,
        };
        PlaceGhost(e.GetPosition(this));
        _dragGhost.IsOpen = true;

        Mouse.Capture(row);
        row.MouseMove         += OnDragMouseMove;
        row.MouseLeftButtonUp += OnDragMouseUp;
        row.LostMouseCapture  += OnDragLostCapture;
        e.Handled = true;
    }

    private void OnDragMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragFromIndex < 0) return;
        PlaceGhost(e.GetPosition(this));
        int newTo = ComputeDropIndex(e.GetPosition(ProjectsList).Y);
        if (newTo != _dragToIndex) { _dragToIndex = newTo; ApplyRowShifts(); }
    }

    private void OnDragMouseUp(object sender, MouseButtonEventArgs e)
    {
        UnhookHandle(sender as FrameworkElement);
        Mouse.Capture(null);
        CommitDrag();
    }

    private void OnDragLostCapture(object sender, MouseEventArgs e)
    {
        UnhookHandle(sender as FrameworkElement);
        CommitDrag();
    }

    private void UnhookHandle(FrameworkElement? h)
    {
        if (h == null) return;
        h.MouseMove         -= OnDragMouseMove;
        h.MouseLeftButtonUp -= OnDragMouseUp;
        h.LostMouseCapture  -= OnDragLostCapture;
    }

    // Placement is relative to ProjectsList and computed via TranslatePoint rather than
    // PointToScreen — PointToScreen returns physical device pixels, but Popup's Horizontal/
    // VerticalOffset are DIPs, so feeding one into the other drifted the ghost away from the
    // cursor on any monitor not running at 100% DPI scale.
    private void PlaceGhost(Point windowPos)
    {
        if (_dragGhost == null || _dragFromIndex < 0) return;
        const double shadowPad = 12;
        double cursorYInList = TranslatePoint(windowPos, ProjectsList).Y;
        _dragGhost.HorizontalOffset = -shadowPad;
        _dragGhost.VerticalOffset   = cursorYInList - _dragCursorOffsetY - shadowPad;
    }

    private int ComputeDropIndex(double cursorYInList)
    {
        double ghostCenter = Math.Clamp(
            cursorYInList - _dragCursorOffsetY + _dragRowHeight / 2,
            _naturalRowTops[0],
            _naturalRowTops[^1] + _dragRowHeight);

        for (int i = 0; i < _naturalRowTops.Length; i++)
        {
            if (ghostCenter < _naturalRowTops[i] + _dragRowHeight / 2)
                return i <= _dragFromIndex ? i : i - 1;
        }
        return _naturalRowTops.Length - 1;
    }

    private void ApplyRowShifts()
    {
        int from = _dragFromIndex;
        int to   = _dragToIndex;
        for (int i = 0; i < _rowElements.Count; i++)
        {
            if (i == from) continue;  // invisible placeholder stays put

            double target =
                (to < from && i >= to && i < from) ?  _dragRowHeight :  // dragging up   → others shift down
                (to > from && i >  from && i <= to) ? -_dragRowHeight : 0; // dragging down → others shift up

            var el = _rowElements[i];
            if (el.RenderTransform is not TranslateTransform tt)
            {
                tt = new TranslateTransform();
                el.RenderTransform = tt;
            }
            if (Math.Abs(tt.Y - target) > 0.5)
                tt.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(target, TimeSpan.FromMilliseconds(180))
                    { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    private Border BuildGhostContent(int fromIndex)
    {
        var p = _projects[fromIndex];

        var dot = new Rectangle { Width = 12, Height = 12, RadiusX = 3, RadiusY = 3, VerticalAlignment = VerticalAlignment.Center };
        ApplySwatchAppearance(dot, p);
        var lbl = new TextBlock
        {
            FontSize = 13, Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var codeRun = new Run(p.Code);
        codeRun.SetResourceReference(TextElement.ForegroundProperty, "Text");
        lbl.Inlines.Add(codeRun);
        if (!string.IsNullOrEmpty(p.Asn))
        {
            var asnRun = new Run($" - {p.Asn}") { FontSize = 11 };
            asnRun.SetResourceReference(TextElement.ForegroundProperty, "TextFaint");
            lbl.Inlines.Add(asnRun);
        }

        // ClipToBounds goes on `row`, not `border` — a Border doesn't clip its child by default
        // even with rounded corners, so long text was bleeding straight past the shape instead
        // of ellipsizing. Clipping `border` itself instead would also crop the DropShadowEffect
        // below, right back to the sharp edge shadowPad exists to avoid.
        var row = new Grid { ClipToBounds = true };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(dot, 0); Grid.SetColumn(lbl, 1);
        row.Children.Add(dot); row.Children.Add(lbl);

        var border = new Border
        {
            Child = row, Padding = new Thickness(8, 6, 8, 6),
            CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1.5),
        };
        border.SetResourceReference(Border.BackgroundProperty,   "Surface2");
        border.SetResourceReference(Border.BorderBrushProperty,  "Accent");
        border.Effect = new System.Windows.Media.Effects.DropShadowEffect
        { BlurRadius = 14, ShadowDepth = 3, Opacity = 0.28, Color = Colors.Black };
        // AllowsTransparency on the host Popup disables ClearType for its content by default;
        // this hints it back on since the ghost always sits over its own opaque background.
        RenderOptions.SetClearTypeHint(border, ClearTypeHint.Enabled);
        return border;
    }

    private void CommitDrag()
    {
        int from = _dragFromIndex;
        int to   = _dragToIndex;
        CleanUpDrag();

        if (from >= 0 && to >= 0 && to != from)
        {
            var proj = _projects[from];
            _projects.RemoveAt(from);
            _projects.Insert(to, proj);
            for (int i = 0; i < _projects.Count; i++) _projects[i].Order = i;
            RebuildProjectRows();
            A.Tracker.UpdateProjects(_projects);
        }
        else if (from >= 0 && from < _rowElements.Count)
        {
            // no move — restore opacity and animate shifts back to zero
            _rowElements[from].Opacity = 1;
            foreach (var el in _rowElements)
                if (el.RenderTransform is TranslateTransform tt)
                    tt.BeginAnimation(TranslateTransform.YProperty,
                        new DoubleAnimation(0, TimeSpan.FromMilliseconds(150))
                        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    private void CleanUpDrag()
    {
        if (_dragGhost is { IsOpen: true }) _dragGhost.IsOpen = false;
        _dragGhost     = null;
        _dragFromIndex = -1;
        _dragToIndex   = -1;
    }

    private void AddProject_Click(object sender, RoutedEventArgs e)
    {
        var p = AddProjectDialog.Ask(this, _projects);
        if (p is not null) { _projects.Add(p); RebuildProjectRows(); A.Tracker.UpdateProjects(_projects); }
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

    // ================= close =================

    // wa.WorkingArea comes back in physical device pixels; Left/Top/ActualWidth are DIPs.
    // Route through CompositionTarget's device<->DIP matrix (same pattern as
    // PillWindow.WorkArea()) instead of mixing the two directly, which misplaced/clipped the
    // window on scaled monitors.
    private void PositionNextToOwner()
    {
        if (Owner is not Window owner) return;
        const double gap = 8;

        var ownerHandle = new System.Windows.Interop.WindowInteropHelper(owner).Handle;
        var wa = System.Windows.Forms.Screen.FromHandle(ownerHandle).WorkingArea;

        var toDip = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var waTopLeft     = toDip.Transform(new Point(wa.Left, wa.Top));
        var waBottomRight = toDip.Transform(new Point(wa.Right, wa.Bottom));

        double left = owner.Left + owner.ActualWidth + gap;
        double top  = owner.Top;
        if (left + ActualWidth > waBottomRight.X)
            left = owner.Left - ActualWidth - gap;

        Left = Math.Max(waTopLeft.X, left);
        Top  = Math.Max(waTopLeft.Y, Math.Min(top, waBottomRight.Y - ActualHeight));
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void StartTutorial_Click(object sender, RoutedEventArgs e)
    {
        Close();
        A.StartTutorial();
    }
    private void TitleBar_Drag(object sender, MouseButtonEventArgs e) { if (e.ChangedButton == MouseButton.Left) DragMove(); }
}
