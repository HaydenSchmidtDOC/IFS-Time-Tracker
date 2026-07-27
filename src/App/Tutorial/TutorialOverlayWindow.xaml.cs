using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace TimeTracker.App.Tutorial;

/// <summary>
/// One page of the guided tour, rendered as a full-monitor, click-blocking overlay: a dimming
/// scrim with a spotlight cutout around the current step's target element (if any), an arrow
/// pointing at it, and a coach-mark card with Skip/Back/Next. Everything is built once in the
/// constructor from a single <see cref="TutorialStep"/> — there is no in-place update path.
///
/// A fresh instance is created per step by TutorialController rather than reused: WPF doesn't
/// allow reassigning a window's Owner once it's been shown, which a persistent overlay would need
/// every time the tour crosses from MainWindow to TimesheetWindow and back. Every ephemeral popup
/// elsewhere in this app (InfoPrompt, NotePrompt, IdlePrompt, ...) is already "new + show once"
/// rather than a persistent Hide/Show window for the same kind of reason, so this just follows
/// that same convention rather than inventing a new one.
/// </summary>
public partial class TutorialOverlayWindow : Window
{
    // Same rationale as PillWindow's own copy of this block (see its remarks) — ShowInTaskbar
    // only hides the taskbar button; WS_EX_TOOLWINDOW is what actually keeps this out of Alt-Tab.
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_APPWINDOW = 0x00040000;
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    public event Action? NextRequested;
    public event Action? BackRequested;
    public event Action? SkipRequested;

    /// <summary>Non-null only when this step both wants a ghost demo (<see cref="TutorialStep.Ghost"/>
    /// != None) and its target resolved to a real, visible element — sized and positioned to
    /// exactly cover that element's on-screen rect, in this window's own local coordinates, so the
    /// controller's ghost-building code can work in small fixed numbers instead of re-deriving
    /// screen geometry itself.</summary>
    public Canvas? GhostLayer { get; private set; }

    private enum Side { Below, Above, Right, Left }

    // Internal, not public — matches TutorialStep's own internal accessibility (the class itself
    // has to stay public: WPF's generated x:Class partial always declares it public, and C# does
    // not allow a partial class's declarations to disagree on that).
    internal TutorialOverlayWindow(Window host, TutorialStep step, int index, int total, FrameworkElement? target)
    {
        InitializeComponent();

        var wa = TimesheetWindow.WorkAreaFor(host);
        Left = wa.Left; Top = wa.Top; Width = wa.Width; Height = wa.Height;
        RootCanvas.Width = Width; RootCanvas.Height = Height; // explicit — Canvas doesn't size itself to fill its parent by default

        // Blocks every click from reaching whatever's underneath, across the WHOLE overlay,
        // including inside the spotlight cutout. A WPF Background="Transparent" trick (as
        // TimesheetWindow's own blank-canvas area uses, see its remarks) is NOT enough here: this
        // window is AllowsTransparency="True", a genuinely layered OS window, and DWM decides
        // click-through for those from the actual COMPOSITED ALPHA at each pixel — bypassing
        // WPF's own hit-test tree entirely. The spotlight cutout is a literal hole in the scrim
        // (zero alpha, nothing painted there by design, so the real UI shows through cleanly) —
        // which made it, of all places, the one part of the overlay that was genuinely click-
        // through at the OS level, letting a real double-click reach the real calendar underneath
        // and open a real AddTimeDialog mid-tour. This rectangle's alpha of 1/255 is imperceptible
        // but non-zero, which is all DWM needs to treat every pixel here as "there".
        var hitTestBlocker = new Rectangle
        {
            Width = Width, Height = Height,
            Fill = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
        };
        RootCanvas.Children.Add(hitTestBlocker);

        var targetLocal = target is { IsVisible: true } ? ToLocalRect(target) : (Rect?)null;
        BuildScrim(targetLocal);
        if (targetLocal is { } ring) BuildSpotlightRing(ring);

        var card = BuildCard(step, index, total);
        RootCanvas.Children.Add(card);
        Canvas.SetZIndex(card, 20);
        card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var cardSize = card.DesiredSize;
        var (cardPos, side) = PlaceCard(targetLocal, cardSize, step.AnchorBottomRight);
        Canvas.SetLeft(card, cardPos.X);
        Canvas.SetTop(card, cardPos.Y);

        if (targetLocal is { } arrowTarget)
            BuildArrow(arrowTarget, new Rect(cardPos, cardSize), side);

        if (step.Ghost != GhostDemo.None && targetLocal is { } ghostRect)
        {
            var layer = new Canvas { Width = ghostRect.Width, Height = ghostRect.Height, ClipToBounds = true, IsHitTestVisible = false };
            Canvas.SetLeft(layer, ghostRect.X);
            Canvas.SetTop(layer, ghostRect.Y);
            Canvas.SetZIndex(layer, 5);
            RootCanvas.Children.Add(layer);
            GhostLayer = layer;
        }

        // Fade the whole overlay in — a fresh window appearing every step would otherwise just
        // snap into existence, reading as far more jarring than the rest of the app's own
        // transitions (CrossFade/SlideIn/AnimateOpenFrom all ease in rather than pop).
        Opacity = 0;
        Loaded += (_, _) => BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

        PreviewKeyDown += OnPreviewKeyDown;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, (exStyle | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW);
    }

    // Esc = Skip, arrow keys/Enter step the tour — lets someone drive the whole thing from the
    // keyboard without hunting for the buttons. Requires this window to actually have input focus
    // (see TutorialController.ShowStepAt, which Activate()s it after Show()).
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: e.Handled = true; SkipRequested?.Invoke(); break;
            case Key.Right or Key.Enter: e.Handled = true; NextRequested?.Invoke(); break;
            case Key.Left: e.Handled = true; BackRequested?.Invoke(); break;
        }
    }

    // ==================== coordinate mapping ====================

    /// <summary>The element's current on-screen rect, converted into THIS window's own local
    /// (top-left = 0,0) DIP coordinate space — every builder below works purely in that local
    /// space so its own numbers don't need to know this overlay's screen position.</summary>
    private Rect ToLocalRect(FrameworkElement el)
    {
        var src = PresentationSource.FromVisual(el);
        var toDip = src?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        // Visual.PointToScreen returns DEVICE pixels, not DIP — the same assumption
        // TimesheetWindow.WorkAreaFor already makes for the same reason (it feeds device pixels
        // into System.Windows.Forms.Screen.FromPoint) — so this has to convert back to DIP before
        // doing any arithmetic against this window's own (DIP) Left/Top.
        var tl = toDip.Transform(el.PointToScreen(new Point(0, 0)));
        var br = toDip.Transform(el.PointToScreen(new Point(el.ActualWidth, el.ActualHeight)));
        return new Rect(new Point(tl.X - Left, tl.Y - Top), new Point(br.X - Left, br.Y - Top));
    }

    private static Rect Inflate(Rect r, double by) => new(r.X - by, r.Y - by, r.Width + by * 2, r.Height + by * 2);
    private static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;

    // ==================== scrim + spotlight ====================

    private void BuildScrim(Rect? targetLocal)
    {
        var outer = new RectangleGeometry(new Rect(0, 0, Width, Height));
        Geometry data = outer;
        if (targetLocal is { } t)
        {
            var hole = new RectangleGeometry(Inflate(t, SpotlightPad), SpotlightRadius, SpotlightRadius);
            data = new CombinedGeometry(GeometryCombineMode.Exclude, outer, hole);
        }
        var scrim = new Path
        {
            Data = data,
            // A fixed near-black regardless of the app's own light/dark theme — dimming
            // everything but the spotlight is the whole point, and a theme-following scrim would
            // barely read as dimmed at all in the light theme.
            Fill = new SolidColorBrush(Color.FromArgb(158, 6, 9, 14)),
            IsHitTestVisible = false, // RootCanvas's own Transparent Background already blocks every click
        };
        RootCanvas.Children.Add(scrim);
    }

    private const double SpotlightPad = 8;
    private const double SpotlightRadius = 10;

    private void BuildSpotlightRing(Rect targetLocal)
    {
        var ring = new Border
        {
            Width = targetLocal.Width + SpotlightPad * 2, Height = targetLocal.Height + SpotlightPad * 2,
            CornerRadius = new CornerRadius(SpotlightRadius), BorderThickness = new Thickness(2),
            IsHitTestVisible = false,
        };
        ring.SetResourceReference(Border.BorderBrushProperty, "Accent");
        Canvas.SetLeft(ring, targetLocal.X - SpotlightPad);
        Canvas.SetTop(ring, targetLocal.Y - SpotlightPad);
        Canvas.SetZIndex(ring, 10);
        RootCanvas.Children.Add(ring);

        // Same idea as TimesheetWindow's BreathingAnimation (a continuous auto-reversing pulse)
        // but without that method's phase-lock-across-rebuilds trick — that trick exists purely to
        // hide the seam of a canvas rebuilding under a live element every second; this window is
        // never rebuilt while showing (a fresh one replaces it per step instead), so there's no
        // seam to hide and a plain repeating animation is all it needs.
        ring.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 1.0, TimeSpan.FromMilliseconds(900))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
    }

    // ==================== card ====================

    private Border BuildCard(TutorialStep step, int index, int total)
    {
        var body = new StackPanel { Margin = new Thickness(18) };

        var eyebrow = new TextBlock
        {
            Text = $"STEP {index + 1} OF {total}", FontSize = 10.5, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6),
        };
        eyebrow.SetResourceReference(TextBlock.ForegroundProperty, "TextFaint");
        body.Children.Add(eyebrow);

        var title = new TextBlock
        {
            Text = step.Title, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        body.Children.Add(title);

        var bodyText = new TextBlock
        {
            Text = step.Body, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 16),
        };
        bodyText.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        body.Children.Add(bodyText);

        var buttonRow = new Grid();
        buttonRow.ColumnDefinitions.Add(new ColumnDefinition());
        buttonRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var skipBtn = new Button { Content = "Skip", HorizontalAlignment = HorizontalAlignment.Left, Style = (Style)FindResource("Btn") };
        skipBtn.Click += (_, _) => SkipRequested?.Invoke();
        Grid.SetColumn(skipBtn, 0);
        buttonRow.Children.Add(skipBtn);

        var rightStack = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (index > 0)
        {
            var backBtn = new Button { Content = "Back", Margin = new Thickness(0, 0, 8, 0), Style = (Style)FindResource("Btn") };
            backBtn.Click += (_, _) => BackRequested?.Invoke();
            rightStack.Children.Add(backBtn);
        }
        var nextBtn = new Button { Content = index == total - 1 ? "Finish" : "Next", Style = (Style)FindResource("BtnAccent") };
        nextBtn.Click += (_, _) => NextRequested?.Invoke();
        rightStack.Children.Add(nextBtn);
        Grid.SetColumn(rightStack, 1);
        buttonRow.Children.Add(rightStack);

        body.Children.Add(buttonRow);

        var card = new Border { Width = 300, CornerRadius = new CornerRadius(12), Child = body };
        card.SetResourceReference(Border.BackgroundProperty, "Surface");
        card.Effect = new System.Windows.Media.Effects.DropShadowEffect
        { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.32, Color = Color.FromRgb(0x20, 0x28, 0x3C) };
        return card;
    }

    /// <summary>Where the card should sit: below the target if there's room, else above, else to
    /// the side — whichever cleanly fits within the overlay's own bounds — and, for a target-less
    /// centered step, dead center. Returns the chosen side too, purely so BuildArrow doesn't have
    /// to re-derive it from the resulting geometry (which side the numbers land on can be
    /// ambiguous right at a boundary; the choice made here is the single source of truth).</summary>
    private (Point Pos, Side Side) PlaceCard(Rect? targetLocal, Size cardSize, bool anchorBottomRight = false)
    {
        const double gap = 8; // between the spotlight ring and the card
        const double margin = 20; // from the overlay's own edges
        if (targetLocal is not { } t)
        {
            var pos = anchorBottomRight
                ? new Point(Width - margin - cardSize.Width, Height - margin - cardSize.Height)
                : new Point((Width - cardSize.Width) / 2, (Height - cardSize.Height) / 2);
            return (pos, Side.Below);
        }

        var ring = Inflate(t, SpotlightPad);
        double belowY = ring.Bottom + gap;
        double aboveY = ring.Top - gap - cardSize.Height;
        double rightX = ring.Right + gap;
        double leftX = ring.Left - gap - cardSize.Width;

        double centerX() => Clamp(ring.X + ring.Width / 2 - cardSize.Width / 2, margin, Math.Max(margin, Width - margin - cardSize.Width));
        double centerY() => Clamp(ring.Y + ring.Height / 2 - cardSize.Height / 2, margin, Math.Max(margin, Height - margin - cardSize.Height));

        if (belowY + cardSize.Height <= Height - margin)
            return (new Point(centerX(), belowY), Side.Below);
        if (aboveY >= margin)
            return (new Point(centerX(), aboveY), Side.Above);
        if (rightX + cardSize.Width <= Width - margin)
            return (new Point(rightX, centerY()), Side.Right);
        if (leftX >= margin)
            return (new Point(leftX, centerY()), Side.Left);

        // Nothing cleanly fits (a very tall/wide target near a screen edge) — clamp below rather
        // than let the card run off-screen or badly overlap the spotlight.
        return (new Point(centerX(), Clamp(belowY, margin, Height - margin - cardSize.Height)), Side.Below);
    }

    // ==================== arrow ====================

    /// <summary>A small filled triangle sitting on whichever of the card's edges faces the target,
    /// pointing back at it — drawn as two overlapping polygons (a thicker Surface-coloured one
    /// behind a thinner Accent-coloured one) so it reads clearly over any background, the same
    /// "dark outline, lighter fill on top" trick CustomCursors uses for its own cursor glyph.</summary>
    private void BuildArrow(Rect targetLocal, Rect cardRect, Side side)
    {
        double cx = Clamp(targetLocal.X + targetLocal.Width / 2, cardRect.X + 10, cardRect.X + cardRect.Width - 10);
        double cy = Clamp(targetLocal.Y + targetLocal.Height / 2, cardRect.Y + 10, cardRect.Y + cardRect.Height - 10);

        const double half = 7, len = 11;
        Point tip, baseA, baseB;
        switch (side)
        {
            case Side.Below: // target sits below the card — arrow rides the card's TOP edge, pointing down at it
                tip = new Point(cx, cardRect.Y - len);
                baseA = new Point(cx - half, cardRect.Y);
                baseB = new Point(cx + half, cardRect.Y);
                break;
            case Side.Above: // target above the card — arrow on the BOTTOM edge, pointing up
                tip = new Point(cx, cardRect.Y + cardRect.Height + len);
                baseA = new Point(cx - half, cardRect.Y + cardRect.Height);
                baseB = new Point(cx + half, cardRect.Y + cardRect.Height);
                break;
            case Side.Right: // target to the right of the card — arrow on the RIGHT edge, pointing right
                tip = new Point(cardRect.X + cardRect.Width + len, cy);
                baseA = new Point(cardRect.X + cardRect.Width, cy - half);
                baseB = new Point(cardRect.X + cardRect.Width, cy + half);
                break;
            default: // Side.Left — target to the left — arrow on the LEFT edge, pointing left
                tip = new Point(cardRect.X - len, cy);
                baseA = new Point(cardRect.X, cy - half);
                baseB = new Point(cardRect.X, cy + half);
                break;
        }

        var points = new PointCollection { tip, baseA, baseB };
        var outline = new Polygon { Points = points, StrokeThickness = 4, StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false };
        outline.SetResourceReference(Shape.FillProperty, "Surface");
        outline.SetResourceReference(Shape.StrokeProperty, "Surface");
        var fill = new Polygon { Points = points, StrokeThickness = 1, IsHitTestVisible = false };
        fill.SetResourceReference(Shape.FillProperty, "Accent");
        fill.SetResourceReference(Shape.StrokeProperty, "Accent");

        Canvas.SetZIndex(outline, 15);
        Canvas.SetZIndex(fill, 16);
        RootCanvas.Children.Add(outline);
        RootCanvas.Children.Add(fill);
    }
}
