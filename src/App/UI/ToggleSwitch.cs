using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace TimeTracker.App.UI;

/// <summary>
/// A small iOS-style on/off switch — a pill track with a sliding circular knob — for a single
/// boolean setting that needs to live outside a settings popover (e.g. pinned to a screen's own
/// corner). Built the same way as <see cref="SegmentedToggle"/> (a hand-assembled visual tree,
/// live-themed via SetResourceReference rather than a templated Style) since this app builds its
/// one-off controls that way rather than via XAML ControlTemplates.
/// </summary>
public sealed class ToggleSwitch
{
    /// <summary>The element to place in a layout.</summary>
    public FrameworkElement Root { get; }

    public bool IsOn { get; private set; }

    /// <summary>Raised when the user flips the switch (not raised by <see cref="SetOn"/>).</summary>
    public event Action<bool>? Toggled;

    private const double TrackWidth = 36;
    private const double TrackHeight = 20;
    private const double KnobSize = 16;
    private const double Inset = 2; // margin between knob and track edge on each side

    private readonly Border _track;
    private readonly Ellipse _knob;
    private readonly TranslateTransform _knobMove = new();

    public ToggleSwitch(bool initialOn = false, string? tooltip = null)
    {
        IsOn = initialOn;

        var outer = new Grid
        {
            Width = TrackWidth, Height = TrackHeight, Cursor = Cursors.Hand,
            Background = Brushes.Transparent, // hit-test the whole track, not just the knob
        };
        if (tooltip is not null) outer.ToolTip = tooltip;

        _track = new Border { CornerRadius = new CornerRadius(TrackHeight / 2) };
        outer.Children.Add(_track);

        _knob = new Ellipse
        {
            Width = KnobSize, Height = KnobSize,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(Inset, 0, 0, 0),
            RenderTransform = _knobMove,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Opacity = 0.3 },
        };
        _knob.SetResourceReference(Shape.FillProperty, "Surface");
        outer.Children.Add(_knob);

        outer.MouseLeftButtonUp += (_, _) => UserToggle(!IsOn);
        outer.SizeChanged += (_, _) => Reposition(animate: false);
        outer.Loaded += (_, _) => Reposition(animate: false);

        ApplyColors();
        Root = outer;
    }

    private void UserToggle(bool on)
    {
        IsOn = on;
        ApplyColors();
        Reposition(animate: true);
        Toggled?.Invoke(on);
    }

    /// <summary>Change state programmatically without raising <see cref="Toggled"/>.</summary>
    public void SetOn(bool on, bool animate = true)
    {
        if (IsOn == on) return;
        IsOn = on;
        ApplyColors();
        Reposition(animate);
    }

    private void ApplyColors() => _track.SetResourceReference(Border.BackgroundProperty, IsOn ? "Accent" : "Surface3");

    private void Reposition(bool animate)
    {
        double targetX = IsOn ? TrackWidth - KnobSize - Inset * 2 : 0;

        // See SegmentedToggle.Reposition for why the current value must be read BEFORE detaching
        // the in-flight animation, not after.
        double currentX = _knobMove.X;
        _knobMove.BeginAnimation(TranslateTransform.XProperty, null);
        if (animate)
        {
            _knobMove.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(currentX, targetX, TimeSpan.FromMilliseconds(160))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        else
        {
            _knobMove.X = targetX;
        }
    }
}
