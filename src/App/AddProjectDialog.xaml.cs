using System.Windows;
using System.Windows.Input;
using TimeTracker.App.UI;
using TimeTracker.Core;

namespace TimeTracker.App;

public partial class AddProjectDialog : Window
{
    // Fixed saturation/lightness for every project colour — only hue varies, via the slider.
    private const double Saturation = 0.55;
    private const double Lightness = 0.50;

    private readonly IReadOnlyList<Project> _existing;
    private readonly Project? _editing; // non-null when editing an existing project
    private string _color = "#3B82C4";
    private Project? _result;

    private AddProjectDialog(IReadOnlyList<Project> existing, Project? editing)
    {
        InitializeComponent();
        _existing = existing;
        _editing = editing;

        double initialHue;
        if (editing is not null)
        {
            HeadingText.Text = "Edit project";
            SaveBtn.Content = "Save changes";
            ColorHelpText.Text = "Drag to change the colour.";
            CodeBox.Text = editing.Code;
            // Code used to be locked here because it was the only identity a historical block
            // could be matched back to — renaming it would have orphaned every past entry's
            // colour/label lookup. Now that Project.Id is the real stable key (see Models.cs)
            // and blocks/state resolve by it first, Code is just another editable field.
            AsnBox.Text = editing.Asn;
            NameBox.Text = editing.Name;
            _color = editing.Color;
            initialHue = ColorUtil.GetHue(ColorUtil.Parse(editing.Color));
        }
        else
        {
            initialHue = FarthestHueFrom(existing);
            _color = ColorUtil.ToHex(ColorUtil.FromHsl(initialHue, Saturation, Lightness));
        }

        // Set the slider's starting position before wiring the handler, so the initial
        // auto-pick (or the edited project's original exact colour) isn't silently
        // recomputed through the fixed saturation/lightness until the user actually drags it.
        HueSlider.Value = initialHue;
        PreviewBrush.Color = ColorUtil.Parse(_color);
        HueSlider.ValueChanged += HueSlider_ValueChanged;

        Loaded += (_, _) => (editing is null ? CodeBox : AsnBox).Focus();
    }

    /// <summary>Add a brand new project. Returns it, or null if cancelled.</summary>
    public static Project? Ask(Window owner, IReadOnlyList<Project> existing)
    {
        var d = new AddProjectDialog(existing, null) { Owner = owner };
        d.ShowDialog();
        return d._result;
    }

    /// <summary>Edit an existing project's code/ASN/name/colour. Returns the updated project, or null if cancelled.</summary>
    public static Project? Edit(Window owner, Project project, IReadOnlyList<Project> others)
    {
        var d = new AddProjectDialog(others, project) { Owner = owner };
        d.ShowDialog();
        return d._result;
    }

    /// <summary>The hue (0-360) whose minimum circular distance to every existing project's hue is largest.</summary>
    private static double FarthestHueFrom(IReadOnlyList<Project> existing)
    {
        if (existing.Count == 0) return 205; // a pleasant default blue
        var hues = existing.Select(p => ColorUtil.GetHue(ColorUtil.Parse(p.Color))).ToList();
        double best = 0, bestScore = -1;
        for (int deg = 0; deg < 360; deg++)
        {
            double minDist = hues.Min(h => CircularDistance(deg, h));
            if (minDist > bestScore) { bestScore = minDist; best = deg; }
        }
        return best;
    }

    private static double CircularDistance(double a, double b)
    {
        double d = Math.Abs(a - b) % 360;
        return d > 180 ? 360 - d : d;
    }

    private void HueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _color = ColorUtil.ToHex(ColorUtil.FromHsl(e.NewValue, Saturation, Lightness));
        PreviewBrush.Color = ColorUtil.Parse(_color);
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var code = CodeBox.Text.Trim();
        var asn = AsnBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(code)) { ShowError("Project code is required."); return; }
        // ASN is optional — IFS doesn't always have one assigned yet when a project is first
        // being tracked, and the export mapping tolerates a blank field.
        if (_existing.Any(p => string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase)))
        { ShowError($"A project with code \"{code}\" already exists."); return; }

        if (_editing is not null)
        {
            _editing.Code = code;
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
    private void Header_Drag(object sender, MouseButtonEventArgs e) { if (e.ChangedButton == MouseButton.Left) DragMove(); }
}
