using System.Globalization;
using System.Text.Json;

namespace TimeTracker.Core;

/// <summary>
/// Receives hook/statusline messages and keeps the per-session records. Stage 1 auto-assigns a new
/// session to the live project (or leaves it unassigned); the UI picker will hook <see cref="SessionStarted"/>.
/// </summary>
public sealed class AiSessionService
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private readonly AppPaths _paths;
    private readonly Func<Settings> _settings;
    private readonly Func<Project?> _liveProject;
    private readonly PricingTable _pricing;
    private readonly Func<DateTime> _utcNow;
    private readonly object _gate = new();
    private readonly HashSet<string> _dirtyMonths = new();
    private List<AiSession> _sessions;

    /// <summary>Raised (outside the lock) when a new session passes the endpoint filter.</summary>
    public event Action<AiSession>? SessionStarted;
    public event Action? Changed;

    public AiSessionService(AppPaths paths, Func<Settings> settings, Func<Project?> liveProject, PricingTable? pricing = null, Func<DateTime>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _paths = paths;
        _settings = settings;
        _liveProject = liveProject;
        _pricing = pricing ?? PricingTable.Load(paths.PricingFile);
        _sessions = Load();
    }

    private IReadOnlyList<AiSession>? _snapshot;

    /// <summary>Cached copy of all sessions, one entry per local day each touched (see <see cref="Slices"/>); rebuilt only after a mutation (every mutation calls Save).</summary>
    public IReadOnlyList<AiSession> Snapshot()
    {
        lock (_gate) return _snapshot ??= _sessions.SelectMany(Slices).ToList();
    }

    public void Handle(AiPipeMessage msg)
    {
        if (string.IsNullOrWhiteSpace(msg.SessionId) || !_settings().AiTrackingEnabled) return;
        switch (msg.Kind)
        {
            case "start": HandleStart(msg); break;
            case "cost": HandleCost(msg); break;
        }
    }

    private void HandleStart(AiPipeMessage msg)
    {
        var s = _settings();
        var cls = EndpointClassifier.Classify(msg.Endpoint ?? new AiEndpointInfo(), s.AiEndpointOverrides);
        if (!s.AiEndpointsToTrack.Any(e => string.Equals(e, cls.ToString(), StringComparison.OrdinalIgnoreCase))) return;

        AiSession? created = null;
        lock (_gate)
        {
            var existing = Find(msg.Provider, msg.SessionId);
            if (existing is not null)
            {
                existing.LastSeenUtc = _utcNow();
                if (!string.IsNullOrEmpty(msg.TranscriptPath)) existing.TranscriptPath = msg.TranscriptPath;
                if (!string.IsNullOrEmpty(msg.Model)) existing.Model = msg.Model;
            }
            else
            {
                var live = _liveProject();
                created = new AiSession
                {
                    Provider = msg.Provider,
                    SessionId = msg.SessionId,
                    ProjectId = live?.Id,
                    ProjectCode = live?.Code,
                    Cwd = msg.Cwd ?? "",
                    Endpoint = cls,
                    StartedUtc = _utcNow(),
                    LastSeenUtc = _utcNow(),
                    Model = msg.Model,
                    TranscriptPath = msg.TranscriptPath,
                };
                _sessions.Add(created);
            }
            // A brand-new session can't be rebuilt from a transcript (its project assignment lives
            // only here), so it's written straight away; a repeat ping only bumps LastSeen.
            if (created is not null) Save(created); else SaveDeferred(existing!);
        }
        if (created is not null) SessionStarted?.Invoke(Clone(created));
        Changed?.Invoke();
    }

    private void HandleCost(AiPipeMessage msg)
    {
        if (msg.TotalCostUsd is not { } total || total < 0) return;
        lock (_gate)
        {
            // Cost pings only update sessions the start filter already admitted.
            var s = Find(msg.Provider, msg.SessionId);
            if (s is null) return;

            // The statusline total is cumulative within a run; a lower value can only mean it reset
            // (e.g. after resume), so bank what we had so no spend is lost either way.
            if (total < s.LastReportedUsd) s.CostBaseUsd += s.LastReportedUsd;
            s.LastReportedUsd = total;
            SetCost(s, s.CostBaseUsd + total, _utcNow().ToLocalTime());
            s.CostSource = "statusline";
            s.LastSeenUtc = _utcNow();
            if (!string.IsNullOrEmpty(msg.Model)) s.Model = msg.Model;
            SaveDeferred(s);
        }
        Changed?.Invoke();
    }

    /// <summary>Recompute cost and token counts from the transcript. Statusline-sourced costs are kept
    /// unless <paramref name="overrideStatusline"/> is set; tokens are always refreshed.</summary>
    public bool RefreshFromTranscript(string provider, string sessionId, bool overrideStatusline = false)
    {
        string? path;
        lock (_gate) path = Find(provider, sessionId)?.TranscriptPath;
        if (string.IsNullOrEmpty(path)) return false;

        var usage = TranscriptParser.Parse(path);
        var cost = _pricing.Cost(usage, out var unpriced);

        lock (_gate)
        {
            var s = Find(provider, sessionId);
            if (s is null) return false;
            s.InputTokens = usage.Sum(u => u.Input);
            s.OutputTokens = usage.Sum(u => u.Output);
            s.CacheReadTokens = usage.Sum(u => u.CacheRead);
            s.CacheWriteTokens = usage.Sum(u => u.CacheWrite5m + u.CacheWrite1h);
            s.HasUnpricedUsage = unpriced;
            if (s.CostSource != "statusline" || overrideStatusline)
            {
                SetCost(s, cost, s.LastSeenUtc.ToLocalTime());
                s.CostSource = "transcript";
            }
            Save(s);
        }
        Changed?.Invoke();
        return true;
    }

    public bool Assign(string provider, string sessionId, Project? project)
    {
        lock (_gate)
        {
            var s = Find(provider, sessionId);
            if (s is null) return false;
            s.ProjectId = project?.Id;
            s.ProjectCode = project?.Code;
            Save(s);
        }
        Changed?.Invoke();
        return true;
    }

    private AiSession? Find(string provider, string id)
        => _sessions.FirstOrDefault(x => x.SessionId == id && x.Provider == provider);

    private static AiSession Clone(AiSession s) => s.ShallowCopy();

    private static string DayKey(DateTime localDay) => localDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Sets the session's cumulative cost, crediting any increase to the local day of
    /// <paramref name="when"/> (a decrease, e.g. a transcript re-price, is taken back off the newest days).</summary>
    private static void SetCost(AiSession s, double newCost, DateTime when)
    {
        if (s.DayCosts.Count == 0 && s.CostUsd > 0) s.DayCosts[DayKey(s.StartedUtc.ToLocalTime())] = s.CostUsd;
        double delta = newCost - s.CostUsd;
        s.CostUsd = newCost;
        if (delta > 0)
        {
            var key = DayKey(when);
            s.DayCosts[key] = s.DayCosts.GetValueOrDefault(key) + delta;
        }
        else if (delta < 0)
        {
            foreach (var key in s.DayCosts.Keys.OrderByDescending(k => k).ToList())
            {
                var take = Math.Min(-delta, s.DayCosts[key]);
                s.DayCosts[key] -= take;
                delta += take;
                if (delta >= 0) break;
            }
        }
    }

    /// <summary>The session as one entry per local day it ran across, each carrying that day's cost (and
    /// a cost-proportional share of the tokens) and its start/end clipped to the day, so charts and
    /// totals bucket by day without knowing sessions can cross midnight. A single-day session is
    /// returned as-is.</summary>
    private static IEnumerable<AiSession> Slices(AiSession s)
    {
        var first = s.StartedUtc.ToLocalTime().Date;
        var last = s.LastSeenUtc.ToLocalTime().Date;
        if (last <= first) { yield return Clone(s); yield break; }

        for (var day = first; day <= last; day = day.AddDays(1))
        {
            double cost = s.DayCosts.Count == 0 ? (day == first ? s.CostUsd : 0) : s.DayCosts.GetValueOrDefault(DayKey(day));
            double share = s.CostUsd > 0 ? cost / s.CostUsd : (day == first ? 1 : 0);
            var slice = Clone(s);
            slice.DayCosts = new();
            slice.CostUsd = cost;
            if (day > first) slice.StartedUtc = DateTime.SpecifyKind(day, DateTimeKind.Local).ToUniversalTime();
            if (day < last) slice.LastSeenUtc = DateTime.SpecifyKind(day.AddDays(1), DateTimeKind.Local).ToUniversalTime();
            slice.InputTokens = (long)Math.Round(s.InputTokens * share);
            slice.OutputTokens = (long)Math.Round(s.OutputTokens * share);
            slice.CacheReadTokens = (long)Math.Round(s.CacheReadTokens * share);
            slice.CacheWriteTokens = (long)Math.Round(s.CacheWriteTokens * share);
            yield return slice;
        }
    }

    private string MonthFile(AiSession s) => _paths.AiSessionsFileFor(s.StartedUtc.ToLocalTime());

    /// <summary>Loads every month file. The old single ai_sessions.json is split into month files on
    /// first run (kept as ai_sessions.json.pre-split.bak).</summary>
    private List<AiSession> Load()
    {
        var all = new List<AiSession>();
        foreach (var file in Directory.GetFiles(_paths.DataFolder, "ai_sessions-*.json").OrderBy(f => f))
            try { all.AddRange(JsonSerializer.Deserialize<List<AiSession>>(File.ReadAllText(file), Opts) ?? new()); } catch { }

        try
        {
            if (File.Exists(_paths.AiSessionsFile))
            {
                var legacy = JsonSerializer.Deserialize<List<AiSession>>(File.ReadAllText(_paths.AiSessionsFile), Opts) ?? new();
                // A crash after writing the month files but before the rename would leave both; keep the month copy.
                foreach (var s in legacy)
                    if (!all.Any(x => x.SessionId == s.SessionId && x.Provider == s.Provider)) all.Add(s);
                _sessions = all;
                foreach (var s in all) _dirtyMonths.Add(MonthFile(s));
                Save();
                var bak = _paths.AiSessionsFile + ".pre-split.bak";
                if (File.Exists(bak)) File.Delete(bak);
                File.Move(_paths.AiSessionsFile, bak);
            }
        }
        catch { }
        return all;
    }

    /// <summary>How long a cost/keep-alive ping may sit unwritten. The statusline reports a
    /// cumulative total, so the next ping restores anything lost, and a transcript refresh can
    /// rebuild the rest — pings arrive far more often than the data actually needs persisting.</summary>
    private static readonly TimeSpan FlushDelay = TimeSpan.FromSeconds(5);
    private System.Threading.Timer? _flushTimer;
    private bool _dirty;

    /// <summary>Marks the session changed and writes them after <see cref="FlushDelay"/>, coalescing
    /// any pings in between. Caller holds the lock.</summary>
    private void SaveDeferred(AiSession s)
    {
        _snapshot = null;
        _dirtyMonths.Add(MonthFile(s));
        _dirty = true;
        _flushTimer ??= new System.Threading.Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        _flushTimer.Change(FlushDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Writes any pending deferred changes now. Called on app exit.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (!_dirty) return;
            try { Save(); } catch { _dirty = true; /* disk hiccup — the next ping re-arms the write */ }
        }
    }

    private void Save(AiSession s)
    {
        _dirtyMonths.Add(MonthFile(s));
        Save();
    }

    /// <summary>Writes the month files that changed since the last save.</summary>
    private void Save()
    {
        _snapshot = null;
        _dirty = false;
        foreach (var file in _dirtyMonths.ToList())
        {
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_sessions.Where(x => MonthFile(x) == file).ToList(), Opts));
            if (File.Exists(file)) File.Replace(tmp, file, null);
            else File.Move(tmp, file);
            _dirtyMonths.Remove(file);
        }
    }
}
