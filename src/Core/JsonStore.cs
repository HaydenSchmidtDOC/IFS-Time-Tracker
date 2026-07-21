using System.Text.Json;

namespace TimeTracker.Core;

/// <summary>Loads/saves projects, live state, and settings as human-readable JSON.</summary>
public sealed class JsonStore
{
    private readonly AppPaths _paths;
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public JsonStore(AppPaths paths) => _paths = paths;

    // ---- Projects ----
    public List<Project> LoadProjects()
    {
        if (!File.Exists(_paths.ProjectsFile)) return new List<Project>();
        try
        {
            var json = File.ReadAllText(_paths.ProjectsFile);
            var list = JsonSerializer.Deserialize<List<Project>>(json, Opts) ?? new();
            return list.OrderBy(p => p.Order).ToList();
        }
        catch { return new List<Project>(); }
    }

    public void SaveProjects(IEnumerable<Project> projects)
        => WriteAtomic(_paths.ProjectsFile, JsonSerializer.Serialize(projects, Opts));

    // ---- State ----
    public TrackerState LoadState()
    {
        if (!File.Exists(_paths.StateFile)) return new TrackerState();
        try
        {
            return JsonSerializer.Deserialize<TrackerState>(File.ReadAllText(_paths.StateFile), Opts)
                   ?? new TrackerState();
        }
        catch { return new TrackerState(); }
    }

    public void SaveState(TrackerState state)
        => WriteAtomic(_paths.StateFile, JsonSerializer.Serialize(state, Opts));

    // ---- Settings ----
    public Settings LoadSettings()
    {
        if (!File.Exists(_paths.SettingsFile))
        {
            var def = new Settings();
            SaveSettings(def);
            return def;
        }
        try
        {
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(_paths.SettingsFile), Opts)
                   ?? new Settings();
        }
        catch { return new Settings(); }
    }

    public void SaveSettings(Settings settings)
        => WriteAtomic(_paths.SettingsFile, JsonSerializer.Serialize(settings, Opts));

    /// <summary>
    /// Write via a temp file + move so a mid-write OneDrive sync can never leave a
    /// half-written JSON file.
    /// </summary>
    private static void WriteAtomic(string path, string contents)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }
}
