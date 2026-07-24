using System.Windows;
using System.Windows.Input;
using TimeTracker.App.UI;
using TimeTracker.Core;

namespace TimeTracker.App;

public partial class NotePrompt : Window
{
    private string? _result;

    private NotePrompt(Project ending, string? explanation)
    {
        InitializeComponent();
        ProjectDot.Fill = ColorUtil.Brush(ending.Color);
        ProjectText.Text = string.IsNullOrWhiteSpace(ending.Name) ? ending.Code : $"{ending.Code} · {ending.Name}";
        // Swapped in for the usual generic blurb when the caller has something more specific to
        // say about WHY this block is ending (see the live-collision auto-stop in App's tick
        // handler) — one window doing double duty as both the explanation and the note prompt,
        // rather than showing a separate heads-up dialog first.
        if (explanation is not null) Sub.Text = explanation;
        Loaded += (_, _) => { NoteBox.Focus(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { _result = null; Close(); } };
    }

    /// <summary>Ask for an optional note. Returns the note text, or null if skipped.</summary>
    public static string? Ask(Project ending, string? explanation = null)
    {
        var w = new NotePrompt(ending, explanation);
        w.ShowDialog();
        return w._result;
    }

    private void Save_Click(object sender, RoutedEventArgs e) { _result = NoteBox.Text.Trim(); Close(); }
    private void Skip_Click(object sender, RoutedEventArgs e) { _result = null; Close(); }
}
