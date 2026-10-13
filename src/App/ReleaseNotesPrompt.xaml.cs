using System.Windows;

namespace TimeTracker.App;

/// <summary>One-time "what's new" popup shown on the first launch after an update — same
/// borderless-dialog shape as WelcomePrompt/UpdatePrompt.</summary>
public partial class ReleaseNotesPrompt : Window
{
    private ReleaseNotesPrompt(string version, string notes)
    {
        InitializeComponent();
        TitleText.Text = $"Release Notes: v{version}";
        NotesText.Text = notes;
    }

    public static void Show(string version, string notes) => new ReleaseNotesPrompt(version, notes).ShowDialog();

    private void Ok_Click(object sender, RoutedEventArgs e) => Close();
}