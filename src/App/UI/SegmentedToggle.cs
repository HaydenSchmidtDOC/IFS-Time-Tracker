using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TimeTracker.App.UI;

/// <summary>
/// A small pill-shaped multi-option toggle — e.g. "Totals / Calendar", "Amount / Times", or a
/// four-way "Light / Dark / System / Custom" — with a sliding accent highlight that eases
/// between segments instead of just snapping. Built as a plain class wrapping a hand-assembled
/// visual tree (matching how the rest of this app builds one-off UI pieces in code, e.g.
/// DayBlocksWindow's rows) rather than a templated Style.
/// </summary>
public sealed class SegmentedToggle
{
    /// <summary>The element to place in a layout.</summary>
    public FrameworkElement Root { get; }

    public int SelectedIndex { get; private set; }

    /// <summary>Raised when the user picks a different segment (not raised by <see cref="SetIndex"/>).</summary>
    public event Action<int>? SelectionChanged;

    private readonly int _count;
    private readonly Border _track;
    private readonly Border _highlight;
    private readonly TranslateTransform _highlightMove = new();
    private readonly TextBlock[] _labels;

    /// <summary>Two-option convenience overload — every use before the four-way theme-mode
    /// toggle was exactly two fixed text segments (Amount/Times, Totals/Calendar), so this keeps
    /// those call sites unchanged.</summary>
    public SegmentedToggle(string leftLabel, string rightLabel, int initialIndex = 0, double fontSize = 12)
        : this(new[] { leftLabel, rightLabel }, initialIndex, fontSize)
    {
    }

    public SegmentedToggle(string[] labels, int initialIndex = 0, double fontSize = 12)
    {
        if (labels.Length < 2)
            throw new ArgumentException("A segmented toggle needs at least two segments.", nameof(labels));
        _count = labels.Length;
        SelectedIndex = initialIndex;

        var outer = new Grid { Height = 30, Cursor = Cursors.Hand, Background = Brushes.Transparent };
        // SetResourceReference (not FindResource-and-assign) on every themed brush below — this
        // control is built once per window and reused for the window's whole lifetime (windows
        // are Hide/Show'd, not recreated), so a one-time FindResource lookup would freeze it at
        // whatever the system accent/light-dark theme happened to be at construction time and
        // never follow a live change (App.OnSystemUserPreferenceChanged re-runs ApplyTheme/
        // ApplySystemAccent, which only actually reaches live UI through resource references
        // like this, not through a value someone already copied out).
        _track = new Border { CornerRadius = new CornerRadius(8) };
        _track.SetResourceReference(Border.BackgroundProperty, "Surface3");
        outer.Children.Add(_track);

        _highlight = new Border
        {
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(3), HorizontalAlignment = HorizontalAlignment.Left,
            RenderTransform = _highlightMove,
        };
        _highlight.SetResourceReference(Border.BackgroundProperty, "Accent");
        outer.Children.Add(_highlight);

        var labelsGrid = new Grid();
        _labels = new TextBlock[_count];
        for (int i = 0; i < _count; i++)
        {
            labelsGrid.ColumnDefinitions.Add(new ColumnDefinition());
            _labels[i] = MakeLabel(labels[i], fontSize);
            Grid.SetColumn(_labels[i], i);
            labelsGrid.Children.Add(_labels[i]);
        }
        outer.Children.Add(labelsGrid);

        // A click anywhere in the pill jumps straight to whichever segment sits under the
        // pointer (not just "the other one") — with exactly two segments that's identical to the
        // old flip-on-any-click behaviour, so existing two-option uses see no change.
        outer.MouseLeftButtonUp += (_, e) =>
        {
            double x = e.GetPosition(outer).X;
            int index = Math.Clamp((int)(x / outer.ActualWidth * _count), 0, _count - 1);
            UserSelect(index);
        };
        outer.SizeChanged += (_, _) => Reposition(animate: false);
        outer.Loaded += (_, _) => Reposition(animate: false);

        ApplyColors();
        Root = outer;
    }

    private static TextBlock MakeLabel(string text, double fontSize) => new()
    {
        Text = text, FontSize = fontSize, FontWeight = FontWeights.Medium,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };

    private void UserSelect(int index)
    {
        if (SelectedIndex == index) return;
        SelectedIndex = index;
        ApplyColors();
        Reposition(animate: true);
        SelectionChanged?.Invoke(index);
    }

    /// <summary>Change the selection programmatically without raising <see cref="SelectionChanged"/>
    /// — e.g. pre-selecting "Times" when a manual-add dialog opens from a calendar double-click.</summary>
    public void SetIndex(int index, bool animate = true)
    {
        if (SelectedIndex == index) return;
        SelectedIndex = index;
        ApplyColors();
        Reposition(animate);
    }

    private void ApplyColors()
    {
        for (int i = 0; i < _count; i++)
            _labels[i].SetResourceReference(TextBlock.ForegroundProperty, i == SelectedIndex ? "AccentInk" : "TextDim");
    }

    private void Reposition(bool animate)
    {
        double segment = _track.ActualWidth / _count;
        if (segment <= 0) return; // not laid out yet — Loaded/SizeChanged will call again once it is
        _highlight.Width = Math.Max(0, segment - 6); // minus the 3px margin on each side
        double targetX = SelectedIndex * segment;

        // Read the CURRENT (possibly still-animating/held) position BEFORE detaching the
        // animation below — reading it after would return X's plain base value instead, which
        // BeginAnimation(prop, null) never updates (an active/held animation overrides the
        // rendered value without ever writing that value back to the base property). Since the
        // base value stays whatever it was set to on construction (0) forever, the bug this
        // caused was direction-specific: 0→1 happened to animate correctly (the stale base, 0,
        // IS the true starting point that direction), but every 1→0 reposition silently used the
        // same stale 0 as both its "from" AND its target, so it just snapped instead of sliding.
        double currentX = _highlightMove.X;
        _highlightMove.BeginAnimation(TranslateTransform.XProperty, null); // stop any in-flight animation first
        if (animate)
        {
            _highlightMove.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(currentX, targetX, TimeSpan.FromMilliseconds(220))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        else
        {
            _highlightMove.X = targetX;
        }
    }
}
