using System.Windows;

namespace TimeTracker.App;

/// <summary>Acknowledgement-only popup — same borderless-dialog shape as NotePrompt/IdlePrompt,
/// but for a message that doesn't need any input or choice, just an "OK". Used for the
/// can't-start collision case (see Tracker.StartRefused) and, when note-prompting is off, the
/// auto-stopped case too — see App.CheckForLiveCollisionWithPrompt, which otherwise folds that
/// explanation into NotePrompt itself rather than showing this as a second, separate popup.</summary>
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
