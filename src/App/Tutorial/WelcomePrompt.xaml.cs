using System.Windows;

namespace TimeTracker.App.Tutorial;

/// <summary>First-run popup — same borderless-dialog shape as IdlePrompt/InfoPrompt — offering
/// the new guided tour. Shown exactly once (see App.OnStartup's OnboardingPromptShown check);
/// the Settings window's own "Start tutorial" button re-runs the tour afterwards without going
/// through this prompt again.</summary>
public partial class WelcomePrompt : Window
{
    private bool _start;

    private WelcomePrompt() => InitializeComponent();

    /// <summary>Show the welcome popup. Returns true if the user chose to start the tour.</summary>
    public static bool Ask()
    {
        var w = new WelcomePrompt();
        w.ShowDialog();
        return w._start;
    }

    private void Start_Click(object sender, RoutedEventArgs e) { _start = true; Close(); }
    private void Skip_Click(object sender, RoutedEventArgs e) { _start = false; Close(); }
}
