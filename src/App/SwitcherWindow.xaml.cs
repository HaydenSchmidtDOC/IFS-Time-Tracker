using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using TimeTracker.App.Interop;
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

    private readonly AiSession? _pickFor;
    private readonly IntPtr _returnFocusTo;

    /// <param name="centerOn">Physical screen pixels (the terminal) to centre over horizontally,
    /// instead of the screen centre.</param>
    public SwitcherWindow(AiSession? pickFor = null, System.Drawing.Rectangle? centerOn = null)
    {
        InitializeComponent();
        _pickFor = pickFor;
        if (centerOn is { } area)
        {
            // Positioned in physical pixels via SetWindowPos (WPF's Left/Top are DIPs of whichever
            // monitor it starts on, which is wrong across mixed DPI). Re-run on every size change:
            // SizeToContent settles the height, and moving onto a different-DPI monitor rescales.
            WindowStartupLocation = WindowStartupLocation.Manual;
            SizeChanged += (_, e) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                // From the new size, not GetWindowRect: SizeChanged fires before WPF has resized
                // the HWND, so its rect is still the old (pre-SizeToContent) size.
                var dpi = VisualTreeHelper.GetDpi(this);
                int w = (int)Math.Round(e.NewSize.Width * dpi.DpiScaleX), h = (int)Math.Round(e.NewSize.Height * dpi.DpiScaleY);
                // Horizontally over the terminal, vertically centred on the terminal's monitor.
                var screen = System.Windows.Forms.Screen.FromRectangle(area).WorkingArea;
                SetWindowPos(hwnd, IntPtr.Zero, area.Left + (area.Width - w) / 2, screen.Top + (screen.Height - h) / 2,
                    0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            };
        }
        foreach (var p in A.Tracker.Projects.Where(p => p.Enabled)) _rows.Add(new SwitchRow(p));
        List.ItemsSource = _rows;

        // Start highlighted on the live project (or the first project).
        var liveId = A.Tracker.Active?.Id;
        _hi = Math.Max(0, _rows.ToList().FindIndex(r => r.Id == liveId));
        UpdateHighlight();

        if (_pickFor is not null)
        {
            // Takes focus so arrows/Enter work immediately (ForceForeground — a plain Activate()
            // from a background app is silently refused while the terminal owns the foreground),
            // then hands focus back to whatever had it once answered. Still stays open until
            // answered rather than dismissing on deactivation.
            _returnFocusTo = ForceForeground.Current();
            Loaded += (_, _) =>
            {
                ForceForeground.Apply(new WindowInteropHelper(this).Handle);
                Activate();
                Focus();
            };
            Closed += (_, _) => { if (_returnFocusTo != IntPtr.Zero) ForceForeground.Apply(_returnFocusTo); };
            PickTitle.Visibility = Visibility.Visible;
            TitleText.Text = _pickFor.Provider.ToUpperInvariant();
            HintText.Text = "Click · Enter · Esc = unassigned";
        }
        else
        {
            Loaded += (_, _) => Activate();
        }
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
                row.BadgeText = on && _pickFor is null ? "■ STOP" : "LIVE";
                if (on && _pickFor is null) liveIsHighlighted = true;
            }
        }
        if (_pickFor is not null) return;
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
        if (_pickFor is not null)
        {
            var picked = _rows.Count == 0 ? null : _rows[_hi].Project;
            _closing = true;
            Close();
            A.AiSessions.Assign(_pickFor.Provider, _pickFor.SessionId, picked);
            return;
        }
        if (_rows.Count == 0) { Close(); return; }
        var chosen = _rows[_hi].Project;
        bool isLive = A.Tracker.IsRunning && chosen.Id == A.Tracker.Active?.Id;

        // Close this window BEFORE stopping/switching, not after — both of those can pop the
        // "add a note" prompt (see App.MaybeAskNote), and closing afterward meant this window
        // was still fully open underneath it, the two stacked on top of each other. chosen/
        // isLive are captured locals, so closing first doesn't lose anything they need.
        _closing = true;
        Close();

        if (isLive) A.StopWithPrompt();
        else A.StartOrSwitch(chosen);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Up:    Move(-1); e.Handled = true; break;
            case Key.Down:  Move(1);  e.Handled = true; break;
            case Key.Enter: Commit(); e.Handled = true; break;
            case Key.Escape: Dismiss(); e.Handled = true; break;
        }
        base.OnKeyDown(e);
    }

    /// <summary>Close without choosing — in pick mode that leaves the session unassigned.</summary>
    public void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        Close();
        if (_pickFor is not null) A.AiSessions.Assign(_pickFor.Provider, _pickFor.SessionId, null);
    }

    private const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private void Window_MouseWheel(object sender, MouseWheelEventArgs e) => Move(e.Delta > 0 ? -1 : 1);

    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SwitchRow row) return;
        _hi = _rows.IndexOf(row);
        UpdateHighlight();
        if (e.ClickCount == 2 || _pickFor is not null) Commit(); // double-click confirms; pick mode takes a single click
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (!_closing && _pickFor is null) Close(); // dismiss like a spotlight when focus is lost
    }
}

public sealed class SwitchRow : INotifyPropertyChanged
{
    public Project Project { get; }
    public string Id => Project.Id;
    public string Code => Project.Code;
    public string AsnText => Project.Asn;
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
