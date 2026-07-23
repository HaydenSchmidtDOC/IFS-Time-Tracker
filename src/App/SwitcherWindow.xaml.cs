using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TimeTracker.App.UI;
using TimeTracker.Core;

namespace TimeTracker.App;

public partial class SwitcherWindow : Window
{
    private readonly ObservableCollection<SwitchRow> _rows = new();
    private int _hi;
    private bool _closing;

    private static App A => App.Current;

    public SwitcherWindow()
    {
        InitializeComponent();
        foreach (var p in A.Tracker.Projects) _rows.Add(new SwitchRow(p));
        List.ItemsSource = _rows;

        // Start highlighted on the live project (or the first project).
        var liveId = A.Tracker.Active?.Id;
        _hi = Math.Max(0, _rows.ToList().FindIndex(r => r.Id == liveId));
        UpdateHighlight();

        Loaded += (_, _) => Activate();
    }

    private void Move(int delta)
    {
        if (_rows.Count == 0) return;
        _hi = (_hi + delta + _rows.Count) % _rows.Count;
        UpdateHighlight();
    }

    private void UpdateHighlight()
    {
        bool liveIsHighlighted = false;
        for (int i = 0; i < _rows.Count; i++)
        {
            bool on = i == _hi;
            var row = _rows[i];
            row.RowBg = on ? Tint(row.Project.Color, 0.18) : Brushes.Transparent;

            bool isLive = A.Tracker.IsRunning && row.Id == A.Tracker.Active?.Id;
            if (isLive)
            {
                // Confirming the already-live row stops tracking instead of a no-op "switch to
                // itself" — flip its badge to spell that out only while it's the highlighted one.
                row.BadgeText = on ? "■ STOP" : "LIVE";
                if (on) liveIsHighlighted = true;
            }
        }
        HintText.Text = liveIsHighlighted ? "Enter to stop" : "↑ ↓ · Enter · dbl-click";
        HintText.Foreground = liveIsHighlighted
            ? (Brush)FindResource("Live")
            : (Brush)FindResource("TextFaint");
    }

    private static Brush Tint(string hex, double alpha)
    {
        var c = ColorUtil.Parse(hex);
        return new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), c.R, c.G, c.B));
    }

    private void Commit()
    {
        if (_rows.Count == 0) { Close(); return; }
        var chosen = _rows[_hi].Project;
        _closing = true;
        bool isLive = A.Tracker.IsRunning && chosen.Id == A.Tracker.Active?.Id;
        if (isLive) A.StopWithPrompt();
        else A.StartOrSwitch(chosen);
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Up:    Move(-1); e.Handled = true; break;
            case Key.Down:  Move(1);  e.Handled = true; break;
            case Key.Enter: Commit(); e.Handled = true; break;
            case Key.Escape: _closing = true; Close(); e.Handled = true; break;
        }
        base.OnKeyDown(e);
    }

    private void Window_MouseWheel(object sender, MouseWheelEventArgs e) => Move(e.Delta > 0 ? -1 : 1);

    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SwitchRow row) return;
        _hi = _rows.IndexOf(row);
        UpdateHighlight();
        if (e.ClickCount == 2) Commit(); // double-click confirms immediately
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (!_closing) Close(); // dismiss like a spotlight when focus is lost
    }
}

public sealed class SwitchRow : INotifyPropertyChanged
{
    public Project Project { get; }
    public string Id => Project.Id;
    public string Code => Project.Code;
    public string AsnText => $"ASN {Project.Asn}";
    public Brush Swatch { get; }
    public Visibility LiveVisibility =>
        App.Current.Tracker.IsRunning && App.Current.Tracker.Active?.Id == Id
            ? Visibility.Visible : Visibility.Collapsed;

    private Brush _rowBg = Brushes.Transparent;
    public Brush RowBg { get => _rowBg; set { _rowBg = value; Notify(nameof(RowBg)); } }

    private string _badgeText = "LIVE";
    public string BadgeText { get => _badgeText; set { _badgeText = value; Notify(nameof(BadgeText)); } }

    public SwitchRow(Project p)
    {
        Project = p;
        Swatch = ColorUtil.Brush(p.Color);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
