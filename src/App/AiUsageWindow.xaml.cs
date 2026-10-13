using System.Windows;
using System.Windows.Interop;
using TimeTracker.App.Interop;
using TimeTracker.Core;

namespace TimeTracker.App;

public partial class AiUsageWindow : Window
{
    private static App A => App.Current;
    private static readonly string[] Periods = { "Today", "This week", "This month", "All time" };

    private bool _loading = true;

    public AiUsageWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(new WindowInteropHelper(this).Handle, !A.IsLightTheme);

        PeriodBox.ItemsSource = Periods;
        PeriodBox.SelectedIndex = 2;
        var providers = new List<string> { "All providers" };
        providers.AddRange(A.AiSessions.Snapshot().Select(s => s.Provider).Distinct().OrderBy(x => x));
        ProviderBox.ItemsSource = providers;
        ProviderBox.SelectedIndex = 0;
        _loading = false;

        A.AiSessions.Changed += OnSessionsChanged;
        Closed += (_, _) => A.AiSessions.Changed -= OnSessionsChanged;
        Reload();
    }

    private void OnSessionsChanged() => Dispatcher.BeginInvoke(() =>
    {
        // Reassigning from a row's own dropdown raises Changed; rebuilding mid-selection would
        // tear the open combo out from under the user, so only refresh when nothing is editing.
        if (!IsKeyboardFocusWithin) Reload();
    });

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading) Reload();
    }

    private (DateTime From, DateTime To) Range()
    {
        var today = DateTime.Today;
        return PeriodBox.SelectedIndex switch
        {
            0 => (today, today),
            1 => (today.AddDays(-(((int)today.DayOfWeek + 6) % 7)), today),
            2 => (new DateTime(today.Year, today.Month, 1), today),
            _ => (DateTime.MinValue, DateTime.MaxValue),
        };
    }

    private void Reload()
    {
        var (from, to) = Range();
        string? provider = ProviderBox.SelectedIndex > 0 ? (string)ProviderBox.SelectedItem : null;
        bool unassignedOnly = UnassignedOnly.IsChecked == true;

        var choices = new List<ProjectChoice> { new(null, "Unassigned") };
        choices.AddRange(A.Tracker.Projects.Where(p => p.Enabled).OrderBy(p => p.Order).Select(p => new ProjectChoice(p, p.Code)));

        var rows = A.AiSessions.Snapshot()
            .Where(s => { var d = AiChartMath.LocalDay(s); return d >= from && d <= to; })
            .Where(s => provider is null || s.Provider == provider)
            .Where(s => !unassignedOnly || s.ProjectId is null)
            .OrderByDescending(s => s.StartedUtc)
            .Select(s => new AiRow(s, choices))
            .ToList();

        Grid.ItemsSource = rows;
        double total = rows.Sum(r => r.Session.CostUsd);
        int unassigned = rows.Count(r => r.Session.ProjectId is null);
        SummaryText.Text = $"{rows.Count} sessions · {AiChartMath.FormatCost(total)}" + (unassigned > 0 ? $" · {unassigned} unassigned" : "");
    }

    private sealed record ProjectChoice(Project? Project, string Label);

    private sealed class AiRow
    {
        public AiSession Session { get; }
        public IReadOnlyList<ProjectChoice> Choices { get; }
        private ProjectChoice _selected;

        public AiRow(AiSession s, IReadOnlyList<ProjectChoice> choices)
        {
            Session = s;
            Choices = choices;
            _selected = choices.FirstOrDefault(c => c.Project?.Id == s.ProjectId) ?? choices[0];
        }

        public string When => Session.StartedUtc.ToLocalTime().ToString("ddd MMM d HH:mm");
        public string Provider => Session.Provider;
        public string Model => Session.Model ?? "";
        public string Endpoint => Session.Endpoint.ToString();
        public string TokensText => (Session.InputTokens + Session.OutputTokens + Session.CacheReadTokens + Session.CacheWriteTokens).ToString("N0");
        public string CostText => AiChartMath.FormatCost(Session.CostUsd) + (Session.HasUnpricedUsage ? "+" : "");

        public ProjectChoice Selected
        {
            get => _selected;
            set
            {
                if (value is null || ReferenceEquals(value, _selected)) return;
                _selected = value;
                App.Current.AiSessions.Assign(Session.Provider, Session.SessionId, value.Project);
            }
        }
    }
}
