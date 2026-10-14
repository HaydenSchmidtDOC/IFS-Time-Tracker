using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using TimeTracker.App.UI;
using TimeTracker.Core;

namespace TimeTracker.App;

/// <summary>Project chooser for an AI session, styled like <see cref="AddTimeDialog"/>.</summary>
public sealed class AiAssignDialog : Window
{
    private bool _closing;
    private Border? _pressed;

    private AiAssignDialog(AiSession s)
    {
        Title = "AI session";
        Width = 340;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        FontFamily = (FontFamily)FindResource("UiFont");

        var list = new StackPanel { Margin = new Thickness(4) };
        list.Children.Add(Row(s, null, "Unassigned", "#7A808C"));
        foreach (var p in App.Current.Tracker.Projects.Where(p => p.Enabled).OrderBy(p => p.Order))
            list.Children.Add(Row(s, p, string.IsNullOrWhiteSpace(p.Name) ? p.Code : $"{p.Code}  ·  {p.Name}", p.Color));

        var header = new Border { Background = Brushes.Transparent, Cursor = Cursors.SizeAll };
        header.MouseLeftButtonDown += (_, e) => { if (e.ChangedButton == MouseButton.Left) DragMove(); };
        header.Child = new TextBlock
        {
            Text = "Assign AI session", FontSize = 15, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("Text"),
        };

        var start = s.StartedUtc.ToLocalTime();
        var end = s.LastSeenUtc.ToLocalTime();
        var summary = new TextBlock
        {
            Text = $"{s.Provider} · {s.Model ?? "unknown model"} · {AiChartMath.FormatCost(s.CostUsd)}{(s.HasUnpricedUsage ? "+" : "")}\n"
                 + $"{start:ddd MMM d} {start:HH:mm}–{end:HH:mm}",
            FontSize = 11, Foreground = (Brush)FindResource("TextFaint"), Margin = new Thickness(0, 6, 0, 0),
        };

        var cancel = new Button { Content = "Cancel", Style = (Style)FindResource("Btn"), Margin = new Thickness(0, 16, 0, 0) };
        cancel.Click += (_, _) => Close();

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(header);
        panel.Children.Add(summary);
        panel.Children.Add(new TextBlock { Text = "PROJECT", Style = (Style)FindResource("EyebrowText"), Margin = new Thickness(0, 14, 0, 6) });
        panel.Children.Add(new Border
        {
            Background = (Brush)FindResource("Surface2"), CornerRadius = new CornerRadius(8),
            Child = new ScrollViewer { MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list },
        });
        panel.Children.Add(cancel);

        Content = new Border
        {
            Background = (Brush)FindResource("Surface"), CornerRadius = new CornerRadius(12), Margin = new Thickness(12),
            Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.32, Color = Color.FromRgb(0x20, 0x28, 0x3C) },
            Child = panel,
        };

        Closing += (_, _) => _closing = true;
        Deactivated += (_, _) => { if (!_closing) Close(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private UIElement Row(AiSession s, Project? p, string label, string color)
    {
        bool current = p?.Id == s.ProjectId;
        var c = ColorUtil.Parse(color);
        var tint = new SolidColorBrush(Color.FromArgb(46, c.R, c.G, c.B));
        var row = new Border
        {
            CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 6, 8, 6), Cursor = Cursors.Hand,
            Background = current ? tint : Brushes.Transparent,
        };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new Ellipse
        {
            Width = 9, Height = 9, Fill = ColorUtil.Brush(color),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
        });
        content.Children.Add(new TextBlock
        {
            Text = label, FontSize = 12.5, Foreground = (Brush)FindResource("Text"),
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        row.Child = content;
        // Commit only on a press that started on this row: the dialog opens from a mouse-down on
        // the timesheet, and that same click's mouse-up would otherwise land on whichever row is
        // under the cursor and assign + close it instantly.
        row.MouseLeftButtonDown += (_, e) => { _pressed = row; e.Handled = true; };
        row.MouseLeftButtonUp += (_, _) =>
        {
            if (_pressed != row) return;
            App.Current.AiSessions.Assign(s.Provider, s.SessionId, p);
            Close();
        };
        return row;
    }

    public static void Show(AiSession s, Window? owner)
    {
        var d = new AiAssignDialog(s) { Owner = owner };
        d.ShowDialog();
    }
}
