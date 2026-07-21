using System.Text.Json.Serialization;

namespace TimeTracker.Core;

/// <summary>A trackable project, matching IFS timesheet identity (code + ASN).</summary>
public sealed class Project
{
    /// <summary>IFS project code, e.g. "BRIDGE-42". Used as the stable key.</summary>
    public string Code { get; set; } = "";

    /// <summary>IFS ASN (activity sequence) number.</summary>
    public string Asn { get; set; } = "";

    /// <summary>Friendly description shown in the UI.</summary>
    public string Name { get; set; } = "";

    /// <summary>Hex swatch colour, e.g. "#2E9E6B", used for the tray/pill/tint.</summary>
    public string Color { get; set; } = "#3B82C4";

    /// <summary>Display order in the switcher / list.</summary>
    public int Order { get; set; }
}

/// <summary>One completed unit of tracked work — a single row in the monthly CSV.</summary>
public sealed class TimeBlock
{
    public string ProjectCode { get; set; } = "";
    public string Asn { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public DateTime StartLocal { get; set; }
    public DateTime EndLocal { get; set; }
    public long DurationSeconds { get; set; }
    public string Notes { get; set; } = "";

    /// <summary>Elapsed hours rounded to 0.1 h (nearest 6 minutes) for IFS export.</summary>
    [JsonIgnore]
    public double DurationHours => Rounding.ToTenthHours(DurationSeconds);
}

/// <summary>Persisted "what is live right now" so a restart resumes cleanly.</summary>
public sealed class TrackerState
{
    /// <summary>Code of the project currently being tracked, or null if stopped.</summary>
    public string? ActiveProjectCode { get; set; }

    /// <summary>UTC start of the current running block, or null if stopped.</summary>
    public DateTime? BlockStartUtc { get; set; }

    /// <summary>Last project tracked, so the pill's Start button can resume after a stop.</summary>
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
