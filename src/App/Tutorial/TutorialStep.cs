using System.Windows;

namespace TimeTracker.App.Tutorial;

/// <summary>Which top-level window a step's target element (and its ghost-demo layer, if any)
/// lives in — TutorialController uses this to know which window the overlay should currently be
/// owned by/positioned over. None means a target-less, centered step (no spotlight/arrow).</summary>
internal enum TutorialHost { MainWindow, TimesheetWindow, None }

/// <summary>Which of the calendar-view gestures (if any) the overlay should mime with a looping
/// ghost animation while this step is showing — see TutorialController's ghost-demo methods.
/// None for every non-calendar step.</summary>
internal enum GhostDemo { None, Add, Move, Resize, Delete }

/// <summary>One page of the guided tour: the card text, which window/element it points at (if
/// any), and what — if anything — needs to happen on entering/leaving it (opening a window,
/// switching views, starting/stopping a ghost animation). Built as a plain data record rather
/// than a UI element itself, so TutorialOverlayWindow stays a dumb renderer of whatever step the
/// controller currently hands it, and the step sequence itself (see TutorialController.BuildSteps)
/// reads as a flat, reviewable list.</summary>
internal sealed class TutorialStep
{
    public required string Title { get; init; }
    public required string Body { get; init; }
    public TutorialHost Host { get; init; } = TutorialHost.None;

    /// <summary>Resolves the element to spotlight/arrow-point-at, evaluated fresh each time this
    /// step is shown (not cached) — the window it targets may not exist/be laid out yet until
    /// OnEnter has run. Null (the default) means a centered card with no spotlight, for steps
    /// that describe something not tied to one on-screen element (e.g. the quick switcher).</summary>
    public Func<FrameworkElement?>? ResolveTarget { get; init; }

    /// <summary>Runs once before this step is shown — reserved for step-specific setup that
    /// TutorialController can't already infer from Host/Ghost alone (in practice: forcing the
    /// Totals/Calendar view before the step that talks about it, since a resumed
    /// Settings.DefaultTimesheetView could otherwise leave the wrong one showing). Opening/closing
    /// the timesheet window itself is NOT done here — TutorialController.EnsureHostVisible derives
    /// that automatically from Host, so it stays correct on Back too, not just forward.</summary>
    public Action? OnEnter { get; init; }

    public GhostDemo Ghost { get; init; } = GhostDemo.None;
}
