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
}
