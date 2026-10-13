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
    private readonly object _gate = new();
    private readonly List<AiSession> _sessions;

    /// <summary>Raised (outside the lock) when a new session passes the endpoint filter.</summary>
    public event Action<AiSession>? SessionStarted;
    public event Action? Changed;

    public AiSessionService(AppPaths paths, Func<Settings> settings, Func<Project?> liveProject, PricingTable? pricing = null)
    {
        _paths = paths;
        _settings = settings;
        _liveProject = liveProject;
        _pricing = pricing ?? PricingTable.Load(paths.PricingFile);
        _sessions = Load();
    }

    private IReadOnlyList<AiSession>? _snapshot;

    /// <summary>Cached copy of all sessions; rebuilt only after a mutation (every mutation calls Save).</summary>
    public IReadOnlyList<AiSession> Snapshot()
    {
        lock (_gate) return _snapshot ??= _sessions.Select(Clone).ToList();
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
                existing.LastSeenUtc = DateTime.UtcNow;
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
                    StartedUtc = DateTime.UtcNow,
                    LastSeenUtc = DateTime.UtcNow,
                    Model = msg.Model,
                    TranscriptPath = msg.TranscriptPath,
                };
                _sessions.Add(created);
            }
            Save();
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
            s.CostUsd = s.CostBaseUsd + total;
            s.CostSource = "statusline";
            s.LastSeenUtc = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(msg.Model)) s.Model = msg.Model;
            Save();
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
                s.CostUsd = cost;
                s.CostSource = "transcript";
            }
            Save();
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
            Save();
        }
        Changed?.Invoke();
        return true;
    }

    private AiSession? Find(string provider, string id)
        => _sessions.FirstOrDefault(x => x.SessionId == id && x.Provider == provider);

    private static AiSession Clone(AiSession s) => s.ShallowCopy();

    private List<AiSession> Load()
    {
        try
        {
            if (File.Exists(_paths.AiSessionsFile))
                return JsonSerializer.Deserialize<List<AiSession>>(File.ReadAllText(_paths.AiSessionsFile), Opts) ?? new();
        }
        catch { }
        return new();
    }

    private void Save()
    {
        _snapshot = null;
        var tmp = _paths.AiSessionsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_sessions, Opts));
        if (File.Exists(_paths.AiSessionsFile)) File.Replace(tmp, _paths.AiSessionsFile, null);
        else File.Move(tmp, _paths.AiSessionsFile);
    }
}
