using System.Diagnostics;
using System.Windows;
using TimeTracker.Core;

namespace TimeTracker.App;

/// <summary>What the user chose in the update prompt.</summary>
public enum UpdateChoice
{
    /// <summary>Download and install now.</summary>
    UpdateNow,
    /// <summary>Skip this time; check again next time the app starts.</summary>
    Later,
    /// <summary>Never ask about this specific version again.</summary>
    SkipVersion,
}

/// <summary>
/// "A new version is available" popup — same borderless-dialog shape as WelcomePrompt/InfoPrompt.
/// Offers to update now, defer, or skip this version. The "View release notes" button opens the
/// GitHub release page in the default browser.
/// </summary>
public partial class UpdatePrompt : Window
{
    private readonly UpdateInfo _info;
    private UpdateChoice _choice = UpdateChoice.Later;

    private UpdatePrompt(UpdateInfo info)
    {
        InitializeComponent();
        _info = info;
        Sub.Text = $"Version {info.Version} is available — you're on {CurrentVersion}. " +
                   "It'll download in the background and apply when you restart.";
        if (string.IsNullOrWhiteSpace(info.ReleaseUrl))
            ReleaseNotesButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>The running app's version, for the prompt text.</summary>
    private static string CurrentVersion
    {
        get
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "?" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>Show the update prompt. Returns the user's choice.</summary>
    public static UpdateChoice Ask(UpdateInfo info)
    {
        var w = new UpdatePrompt(info);
        w.ShowDialog();
        return w._choice;
    }

    private void UpdateNow_Click(object sender, RoutedEventArgs e) { _choice = UpdateChoice.UpdateNow; Close(); }
    private void Later_Click(object sender, RoutedEventArgs e) { _choice = UpdateChoice.Later; Close(); }
    private void Skip_Click(object sender, RoutedEventArgs e) { _choice = UpdateChoice.SkipVersion; Close(); }

    private void ReleaseNotes_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(_info.ReleaseUrl) { UseShellExecute = true }); }
        catch { /* best effort — opening a browser must never crash the prompt */ }
    }
}