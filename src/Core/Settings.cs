namespace TimeTracker.Core;

/// <summary>One output column in the IFS export: the header IFS expects, and where its value comes from.</summary>
public sealed class MappingEntry
{
    /// <summary>The exact column header IFS expects in the CSV.</summary>
    public string Header { get; set; } = "";

    /// <summary>
    /// Internal field token to pull the value from: Date, ProjectCode, Asn, ProjectName,
    /// StartLocal, EndLocal, DurationSeconds, DurationHours, Notes. A literal value can be
    /// emitted with the "=literal" form (e.g. "=DOC" to hard-code a column).
    /// </summary>
    public string Field { get; set; } = "";
}

/// <summary>
/// User settings, persisted to <c>data/settings.json</c>. Covers the configurable
/// IFS export mapping, idle behaviour, and where data lives.
/// </summary>
public sealed class Settings
{
    /// <summary>
    /// Optional override for the data folder. When null/empty, the folder next to the
    /// executable (this OneDrive repo's <c>data\</c>) is used so it syncs automatically.
    /// </summary>
    public string? DataFolderOverride { get; set; }

    /// <summary>Minutes of no input (or a lock) before the idle prompt is offered on return.</summary>
    public int IdleThresholdMinutes { get; set; } = 10;

    /// <summary>Whether to prompt for an optional note when a block ends.</summary>
    public bool PromptForNote { get; set; } = true;

    /// <summary>Show the always-on-top pill.</summary>
    public bool PillVisible { get; set; } = true;

    /// <summary>Show the system-tray icon.</summary>
    public bool ShowTrayIcon { get; set; } = true;

    /// <summary>Start with the main window hidden (tray/pill only) rather than shown.</summary>
    public bool StartMinimized { get; set; } = false;

    /// <summary>
    /// IFS export column mapping, in output column order. Refine the headers once a real
    /// IFS timesheet export is available — this is a settings edit, not a code change.
    /// </summary>
    public List<MappingEntry> IfsExportMapping { get; set; } = new()
    {
        new() { Header = "Project ID",   Field = "ProjectCode" },
        new() { Header = "Activity Seq", Field = "Asn" },
        new() { Header = "Date",         Field = "Date" },
        new() { Header = "Hours",        Field = "DurationHours" },
        new() { Header = "Note",         Field = "Notes" },
    };

    /// <summary>Date format used in the exported "Date" column.</summary>
    public string ExportDateFormat { get; set; } = "yyyy-MM-dd";

    /// <summary>
    /// Timesheet chart display-only threshold (hours): a day's project segments below this are
    /// folded into one neutral "Other" bar segment (tooltip shows the exact breakdown) so a day
    /// touching many small projects doesn't turn into a wall of slivers. 0 disables folding —
    /// every segment always shows individually. Never affects recorded data or the IFS export,
    /// only how the chart draws it.
    /// </summary>
    public double ChartMinSegmentHours { get; set; } = 0.25;

    /// <summary>
    /// Timesheet chart display-only grouping: when true (default), every session for a project
    /// on a given day is summed into one bar segment, regardless of gaps or notes — the original
    /// behaviour. When false, only strictly back-to-back sessions (nothing else logged between
    /// them) with the same note collapse together; the same project touched again later in the
    /// day, or with a different note, gets its own separate segment in the stack. Never affects
    /// recorded data or the IFS export, only how the chart draws it.
    /// </summary>
    public bool ChartMergeAllSessions { get; set; } = true;

    /// <summary>Which timesheet view ("Bar"/hours-totalled or "Calendar"/24h time-of-day) was
    /// last selected via the in-window toggle — written every time it's switched, so the next
    /// open just resumes wherever the previous session left off, rather than a manually
    /// configured fixed default.</summary>
    public string DefaultTimesheetView { get; set; } = "Bar";

    /// <summary>
    /// Calendar-view dragging snaps to the nearest this-many minutes — covers resizing a block's
    /// top/bottom edge, moving a whole block, and drawing a new one via drag-to-add, all with the
    /// one shared granularity rather than each having its own. Purely an editing aid — has no
    /// effect on the bar view or anything already recorded.
    /// </summary>
    public int DragSnapMinutes { get; set; } = 5;

    /// <summary>
    /// When a Times-mode add cleanly spans one or more whole existing blocks (see
    /// AddTimeDialog.TryParseTimesMode), it's split into separate entries around them — but a gap
    /// too short to bother with (e.g. two existing blocks a minute apart) is just absorbed rather
    /// than becoming its own sliver row. This is that "too short" threshold, in minutes. 0 turns
    /// it off — every non-zero gap becomes its own entry.
    /// </summary>
    public int MinSplitBlockMinutes { get; set; } = 5;
}
