using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using TimeTracker.App.UI;
using TimeTracker.Core;

namespace TimeTracker.App;

/// <summary>Lists one day's individual recorded blocks (chronological), with a confirm-then-delete
/// per row. Opened by clicking a day's label in the timesheet chart.</summary>
public partial class DayBlocksWindow : Window
{
    private static App A => App.Current;
    private readonly DateTime _day;
    private readonly Action _onChanged;

    public DayBlocksWindow(DateTime day, Action onChanged)
    {
        InitializeComponent();
        _day = day.Date;
        _onChanged = onChanged;
        DateHeading.Text = _day.ToString("dddd, d MMMM");
        Rebuild();
    }

    private void Rebuild()
    {
        RowsPanel.Children.Clear();
        var blocks = A.Log.ReadRange(_day, _day)
            .Where(b => b.StartLocal.Date == _day)
            .OrderBy(b => b.StartLocal)
            .ToList();

        EmptyHint.Visibility = blocks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var b in blocks) RowsPanel.Children.Add(BuildRow(b));
    }

    private UIElement BuildRow(TimeBlock b)
    {
        var project = A.Tracker.FindByCode(b.ProjectCode);
        var color = project is not null ? ColorUtil.Brush(project.Color) : (Brush)FindResource("TextFaint");

        var normal = new Grid();
        normal.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        normal.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        normal.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        normal.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new Ellipse { Width = 9, Height = 9, Fill = color, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        Grid.SetColumn(dot, 0);

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text = $"{b.ProjectCode}   {b.StartLocal:HH:mm}–{b.EndLocal:HH:mm}",
            FontSize = 12.5, FontWeight = FontWeights.Medium, Foreground = (Brush)FindResource("Text"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (!string.IsNullOrWhiteSpace(b.Notes))
        {
            info.Children.Add(new TextBlock
            {
                Text = b.Notes, FontSize = 11, Foreground = (Brush)FindResource("TextFaint"),
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0),
            });
        }
        Grid.SetColumn(info, 1);

        var hours = new TextBlock
        {
            Text = $"{b.DurationHours:0.0}h", FontSize = 12, Foreground = (Brush)FindResource("TextDim"),
            FontFamily = (FontFamily)FindResource("MonoFont"), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
        };
        Grid.SetColumn(hours, 2);

        var delBtn = new Button { Content = "✕", Style = (Style)FindResource("IconBtn"), FontSize = 11, Padding = new Thickness(7, 3, 7, 3) };
        Grid.SetColumn(delBtn, 3);

        normal.Children.Add(dot);
        normal.Children.Add(info);
        normal.Children.Add(hours);
        normal.Children.Add(delBtn);

        // Confirm state — swapped in over "normal" rather than a separate blocking dialog, so
        // deleting a record stays a deliberate two-click action without extra window ceremony.
        var confirm = new Grid { Visibility = Visibility.Collapsed };
        confirm.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        confirm.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        confirm.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var confirmText = new TextBlock
        {
            Text = "Delete this block?", FontSize = 12, FontWeight = FontWeights.Medium,
            Foreground = (Brush)FindResource("Live"), VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(confirmText, 0);

        var cancelBtn = new Button { Content = "Cancel", Style = (Style)FindResource("Btn"), FontSize = 11, Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(0, 0, 6, 0) };
        Grid.SetColumn(cancelBtn, 1);

        var deleteBtn = new Button { Content = "Delete", Style = (Style)FindResource("BtnAccent"), FontSize = 11, Padding = new Thickness(9, 4, 9, 4), Background = (Brush)FindResource("Live") };
        Grid.SetColumn(deleteBtn, 2);

        confirm.Children.Add(confirmText);
        confirm.Children.Add(cancelBtn);
        confirm.Children.Add(deleteBtn);

        var outer = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 4), Background = (Brush)FindResource("Surface2"),
        };
        var host = new Grid();
        host.Children.Add(normal);
        host.Children.Add(confirm);
        outer.Child = host;

        delBtn.Click += (_, _) => { normal.Visibility = Visibility.Collapsed; confirm.Visibility = Visibility.Visible; };
        cancelBtn.Click += (_, _) => { confirm.Visibility = Visibility.Collapsed; normal.Visibility = Visibility.Visible; };
        deleteBtn.Click += (_, _) =>
        {
            A.Log.DeleteBlock(b);
            RowsPanel.Children.Remove(outer);
            if (RowsPanel.Children.Count == 0) EmptyHint.Visibility = Visibility.Visible;
            _onChanged();
        };

        return outer;
    }

    private void Header_Drag(object sender, MouseButtonEventArgs e) { if (e.ChangedButton == MouseButton.Left) DragMove(); }
    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
}
