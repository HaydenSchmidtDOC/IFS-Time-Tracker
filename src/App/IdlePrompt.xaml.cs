using System.Windows;
using TimeTracker.Core;

namespace TimeTracker.App;

public partial class IdlePrompt : Window
{
    private bool _keep = true;

    private IdlePrompt(Project project, TimeSpan away)
    {
        InitializeComponent();
        var mins = Math.Max(1, (int)Math.Round(away.TotalMinutes));
        Sub.Text = $"Away for {mins} min while tracking {project.Code}. Keep that time on the timesheet?";
    }

    /// <summary>Ask whether to keep the idle span. Returns true to keep, false to discard.</summary>
    public static bool Ask(Project project, TimeSpan away)
    {
        var w = new IdlePrompt(project, away);
        w.ShowDialog();
        return w._keep;
    }

    private void Keep_Click(object sender, RoutedEventArgs e) { _keep = true; Close(); }
    private void Discard_Click(object sender, RoutedEventArgs e) { _keep = false; Close(); }
}
