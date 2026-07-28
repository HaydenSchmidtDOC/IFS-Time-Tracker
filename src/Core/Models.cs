using System.Text.Json.Serialization;

namespace TimeTracker.Core;

/// <summary>A trackable project, matching IFS timesheet identity (code + ASN).</summary>
public sealed class Project
{
    /// <summary>
    /// Stable internal identity, assigned once and never changed — this, not <see cref="Code"/>,
    /// is what historical blocks and live state actually reference, so Code/Name/Asn can all be
    /// edited freely (e.g. correcting a typo, or IFS renumbering a project) without corrupting
    /// history or losing the resume-on-restart pointer. Projects loaded from before this field
    /// existed get one back-filled on load (see JsonStore.LoadProjects); new projects get one in
    /// TrackerService.AddProject/UpdateProjects.
    /// </summary>
    public string Id { get; set; } = "";

    /// <summary>IFS project code, e.g. "BRIDGE-42". Editable — see <see cref="Id"/> for the actual stable key.</summary>
    public string Code { get; set; } = "";

    /// <summary>IFS ASN (activity sequence) number. Optional.</summary>
    public string Asn { get; set; } = "";

    /// <summary>Friendly description shown in the UI.</summary>
    public string Name { get; set; } = "";

    /// <summary>Hex swatch colour, e.g. "#2E9E6B", used for the tray/pill/tint.</summary>
    public string Color { get; set; } = "#3B82C4";

    /// <summary>Display order in the switcher / list.</summary>
    public int Order { get; set; }

    /// <summary>Whether this project shows up in the main window and switcher. Disabled
    /// projects keep their history and stay editable from Settings — this only hides them
    /// from the pick-a-project-to-track surfaces.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>One completed unit of tracked work — a single row in the monthly CSV.</summary>
public sealed class TimeBlock
{
    /// <summary>
    /// Stable identity for this block, so it can be found again for delete/update without
    /// relying on its timestamps staying exact (which drag-to-edit will change). Rows written
    /// before this field existed have no stored id; CsvLog synthesizes a deterministic one from
    /// their code+start+end on read, so old rows can still be targeted, just less robustly than
    /// new ones (that synthetic id changes if the row's own text changes).
    /// </summary>
    public string Id { get; set; } = "";

    /// <summary>
    /// Id of the <see cref="Project"/> this block was recorded against. Empty for rows logged
    /// before project ids existed — those fall back to matching on <see cref="ProjectCode"/>
    /// (see TrackerService.FindByBlock).
    /// </summary>
    public string ProjectId { get; set; } = "";

    public string ProjectCode { get; set; } = "";
    public string Asn { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public DateTime StartLocal { get; set; }
    public DateTime EndLocal { get; set; }
    public long DurationSeconds { get; set; }
    public string Notes { get; set; } = "";

    /// <summary>
    /// True for a block added "by amount" with no specific clock time — StartLocal/EndLocal
    /// still hold a nominal placeholder (midnight-anchored) so duration math, the month-file
    /// lookup, and every existing consumer keep working unmodified; this flag is what tells the
    /// calendar view (and anything doing overlap/collision checks) that the placeholder isn't a
    /// real position and should be treated as "just a total for the day," not a time slot.
    /// </summary>
    public bool Unscheduled { get; set; }

    /// <summary>Elapsed hours rounded to 0.1 h (nearest 6 minutes) for IFS export.</summary>
    [JsonIgnore]
    public double DurationHours => Rounding.ToTenthHours(DurationSeconds);
}

/// <summary>Persisted "what is live right now" so a restart resumes cleanly.</summary>
public sealed class TrackerState
{
    /// <summary>Id of the project currently being tracked, or null if stopped.</summary>
    public string? ActiveProjectId { get; set; }

    /// <summary>
    /// Code of the project currently being tracked, kept alongside <see cref="ActiveProjectId"/>
    /// purely so state.json stays human-readable — resolution always prefers the Id, falling
    /// back to this only for a state.json saved before ids existed.
    /// </summary>
    public string? ActiveProjectCode { get; set; }

    /// <summary>UTC start of the current running block, or null if stopped.</summary>
    public DateTime? BlockStartUtc { get; set; }

    /// <summary>Id of the last project tracked, so the pill's Start button can resume after a stop.</summary>
    public string? LastActiveProjectId { get; set; }

    /// <summary>Code counterpart of <see cref="LastActiveProjectId"/> — see its remarks.</summary>
    public string? LastActiveProjectCode { get; set; }

    /// <summary>Whether the floating pill is shown.</summary>
    public bool PillVisible { get; set; } = true;

    /// <summary>Last saved pill screen position (device-independent px). null = default corner.</summary>
    public double? PillLeft { get; set; }
    public double? PillTop { get; set; }
}

/// <summary>Shared rounding helpers so the same rule is used everywhere.</summary>
public static class Rounding
{
    /// <summary>Round seconds to decimal hours at 0.1 h granularity (nearest 6 min).</summary>
    public static double ToTenthHours(long seconds)
        => Math.Round(seconds / 3600.0, 1, MidpointRounding.AwayFromZero);
}
