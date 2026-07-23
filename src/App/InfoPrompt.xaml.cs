using System.Windows;

namespace TimeTracker.App;

/// <summary>Acknowledgement-only popup — same borderless-dialog shape as NotePrompt/IdlePrompt,
/// but for a message that doesn't need any input or choice, just an "OK". Used for the two
/// tracking-collision cases (can't start, session auto-stopped) — see App.StartOrSwitch and
/// App's tick handler.</summary>
public partial class InfoPrompt : Window
{
    private InfoPrompt(string title, string message)
    {
        InitializeComponent();
        TitleText.Text = title;
        Sub.Text = message;
    }

    public static void Show(string title, string message)
    {
        var w = new InfoPrompt(title, message);
        w.ShowDialog();
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Close();
}
