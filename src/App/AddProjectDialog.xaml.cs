using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using TimeTracker.App.UI;
using TimeTracker.Core;

namespace TimeTracker.App;

public partial class AddProjectDialog : Window
{
    // Categorical palette (distinct hues that read on light + dark).
    private static readonly string[] Palette =
    {
        "#2E9E6B", "#3B82C4", "#DE8E2C", "#DF4F68", "#8368D4",
        "#2AA198", "#E5484D", "#64748B", "#6FA82F", "#E8590C",
    };

    private readonly IReadOnlyList<Project> _existing;
    private readonly Project? _editing; // non-null when editing an existing project
    private string _color = Palette[0];
    private Project? _result;
    private readonly List<Border> _swatchBorders = new();

    private AddProjectDialog(IReadOnlyList<Project> existing, Project? editing)
    {
        InitializeComponent();
        _existing = existing;
        _editing = editing;

        if (editing is not null)
        {
            HeadingText.Text = "Edit project";
            SaveBtn.Content = "Save changes";
            CodeBox.Text = editing.Code;
            CodeBox.IsEnabled = false; // code is the stable key used in the log; keep it fixed
            CodeBox.Opacity = 0.65;
            AsnBox.Text = editing.Asn;
            NameBox.Text = editing.Name;
            _color = editing.Color;
        }
        else
        {
            // Pick the first palette colour not already used, for convenience.
            _color = Palette.FirstOrDefault(c => existing.All(p => !string.Equals(p.Color, c, StringComparison.OrdinalIgnoreCase)))
                     ?? Palette[0];
        }

        BuildSwatches();
        Loaded += (_, _) => (editing is null ? CodeBox : AsnBox).Focus();
    }

    /// <summary>Add a brand new project. Returns it, or null if cancelled.</summary>
    public static Project? Ask(Window owner, IReadOnlyList<Project> existing)
    {
        var d = new AddProjectDialog(existing, null) { Owner = owner };
        d.ShowDialog();
        return d._result;
    }

    /// <summary>Edit an existing project's ASN/name/colour (code stays fixed). Returns the updated project, or null if cancelled.</summary>
    public static Project? Edit(Window owner, Project project, IReadOnlyList<Project> others)
    {
        var d = new AddProjectDialog(others, project) { Owner = owner };
        d.ShowDialog();
        return d._result;
    }

    private void BuildSwatches()
    {
        foreach (var hex in Palette)
        {
            var dot = new Rectangle { Width = 24, Height = 24, RadiusX = 6, RadiusY = 6, Fill = ColorUtil.Brush(hex) };
            var border = new Border
            {
                Child = dot, Margin = new Thickness(0, 0, 8, 8), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(2), BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand,
                Tag = hex,
            };
            border.MouseLeftButtonUp += (_, _) => SelectColor(hex);
            _swatchBorders.Add(border);
            Swatches.Children.Add(border);
        }
        HighlightSelected();
    }

    private void SelectColor(string hex) { _color = hex; HighlightSelected(); }

    private void HighlightSelected()
    {
        foreach (var b in _swatchBorders)
            b.BorderBrush = (string)b.Tag == _color ? (Brush)FindResource("Accent") : Brushes.Transparent;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var code = CodeBox.Text.Trim();
        var asn = AsnBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(code)) { ShowError("Project code is required."); return; }
        if (string.IsNullOrWhiteSpace(asn)) { ShowError("ASN number is required."); return; }
        if (_editing is null && _existing.Any(p => string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase)))
        { ShowError($"A project with code \"{code}\" already exists."); return; }

        if (_editing is not null)
        {
            _editing.Asn = asn;
            _editing.Name = NameBox.Text.Trim();
            _editing.Color = _color;
            _result = _editing;
        }
        else
        {
            _result = new Project { Code = code, Asn = asn, Name = NameBox.Text.Trim(), Color = _color };
        }
        Close();
    }

    private void ShowError(string msg) { ErrorText.Text = msg; ErrorText.Visibility = Visibility.Visible; }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
