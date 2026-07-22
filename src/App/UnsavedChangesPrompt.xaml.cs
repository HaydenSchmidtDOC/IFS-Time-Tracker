using System.Windows;

namespace TimeTracker.App;

public enum UnsavedChangesResult { Cancel, Discard, Save }

public partial class UnsavedChangesPrompt : Window
{
    private UnsavedChangesResult _result = UnsavedChangesResult.Cancel;

    private UnsavedChangesPrompt() => InitializeComponent();

    public static UnsavedChangesResult Ask(Window owner)
    {
        var w = new UnsavedChangesPrompt { Owner = owner };
        w.ShowDialog();
        return w._result;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) { _result = UnsavedChangesResult.Cancel; Close(); }
    private void Discard_Click(object sender, RoutedEventArgs e) { _result = UnsavedChangesResult.Discard; Close(); }
    private void Save_Click(object sender, RoutedEventArgs e) { _result = UnsavedChangesResult.Save; Close(); }
}
