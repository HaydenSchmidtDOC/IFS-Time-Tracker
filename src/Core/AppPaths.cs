namespace TimeTracker.Core;

/// <summary>
/// Resolves where data files live. By default this is a <c>data\</c> folder next to the
/// executable — which, because the app ships inside this OneDrive repo, syncs automatically
/// and is the same location a future phone app will read/write.
/// </summary>
public sealed class AppPaths
{
    public string DataFolder { get; }

    public AppPaths(string? overrideFolder = null)
    {
        if (!string.IsNullOrWhiteSpace(overrideFolder))
        {
            DataFolder = overrideFolder!;
        }
        else
        {
            // AppContext.BaseDirectory is the app's folder (works for single-file publish too).
            var root = AppContext.BaseDirectory;
            DataFolder = Path.Combine(root, "data");
        }
        Directory.CreateDirectory(DataFolder);
    }

    public string ProjectsFile => Path.Combine(DataFolder, "projects.json");
    public string StateFile     => Path.Combine(DataFolder, "state.json");
    public string SettingsFile  => Path.Combine(DataFolder, "settings.json");

    /// <summary>Monthly rolling log, e.g. <c>log-2026-07.csv</c>.</summary>
    public string LogFileFor(DateTime localDate)
        => Path.Combine(DataFolder, $"log-{localDate:yyyy-MM}.csv");

    /// <summary>Export target for a given month, e.g. <c>ifs-export-2026-07.csv</c>.</summary>
    public string ExportFileFor(int year, int month)
        => Path.Combine(DataFolder, $"ifs-export-{year:D4}-{month:D2}.csv");
}
