namespace TimeTracker.Core;

/// <summary>
/// The single source of truth for tracking. Owns the project list and the one live block,
/// banks completed blocks to the monthly CSV, and persists live state so a restart resumes.
/// UI-free and clock-injectable so start/switch/stop and idle handling are unit-testable.
/// </summary>
public sealed class TrackerService
{
    private readonly JsonStore _store;
    private readonly CsvLog _log;
    private readonly Func<DateTime> _utcNow;

    private TrackerState _state;

    public List<Project> Projects { get; private set; }

    /// <summary>The project currently being tracked, or null when stopped.</summary>
    public Project? Active { get; private set; }

    /// <summary>True while a block is running.</summary>
    public bool IsRunning => Active is not null && _state.BlockStartUtc is not null;

    /// <summary>Raised whenever the active project or running state changes.</summary>
    public event Action? Changed;

    /// <summary>Raised after a block is banked to the log (so the UI can refresh totals).</summary>
    public event Action<TimeBlock>? BlockLogged;

    public TrackerService(JsonStore store, CsvLog log, Func<DateTime>? utcNow = null)
    {
        _store = store;
        _log = log;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);

        Projects = _store.LoadProjects();
        _state = _store.LoadState();

        // Resume a block that was live when the app last closed.
        if (_state.ActiveProjectCode is not null && _state.BlockStartUtc is not null)
            Active = FindByCode(_state.ActiveProjectCode);
        if (Active is null) { _state.ActiveProjectCode = null; _state.BlockStartUtc = null; }
    }

    // ---------- projects ----------

    public Project? FindByCode(string code)
        => Projects.FirstOrDefault(p => string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase));

    /// <summary>The last project that was tracked (for resuming after a stop), if it still exists.</summary>
    public Project? LastActive
        => _state.LastActiveProjectCode is { } c ? FindByCode(c) : null;

    /// <summary>Resume tracking the last project (used by the pill's Start toggle when stopped).</summary>
    public void ResumeLast()
    {
        var p = LastActive ?? Projects.FirstOrDefault();
        if (p is not null) StartOrSwitch(p);
    }

    public void AddProject(Project p)
    {
        p.Order = Projects.Count == 0 ? 0 : Projects.Max(x => x.Order) + 1;
        Projects.Add(p);
        _store.SaveProjects(Projects);
        Changed?.Invoke();
    }

    public void UpdateProjects(IEnumerable<Project> projects)
    {
        Projects = projects.ToList();
        _store.SaveProjects(Projects);
        // If the active project was removed, stop.
        if (Active is not null && FindByCode(Active.Code) is null) Stop();
        Changed?.Invoke();
    }

    // ---------- tracking ----------

    /// <summary>Elapsed seconds of the current running block (0 when stopped).</summary>
    public long CurrentElapsedSeconds
    {
        get
        {
            if (_state.BlockStartUtc is not DateTime start) return 0;
            var s = (long)(_utcNow() - start).TotalSeconds;
            return s < 0 ? 0 : s;
        }
    }

    /// <summary>Begin tracking a project. Auto-banks any running block first (with optional note).</summary>
    public void StartOrSwitch(Project project, string notes = "")
    {
        if (IsRunning && Active is not null && Active.Code == project.Code) return; // already live
        if (IsRunning) BankCurrent(_utcNow(), notes);
        Active = project;
        _state.ActiveProjectCode = project.Code;
        _state.LastActiveProjectCode = project.Code;
        _state.BlockStartUtc = _utcNow();
        _store.SaveState(_state);
        Changed?.Invoke();
    }

    /// <summary>Stop tracking, banking the current block with an optional note.</summary>
    public void Stop(string notes = "")
    {
        if (IsRunning) BankCurrent(_utcNow(), notes);
        Active = null;
        _state.ActiveProjectCode = null;
        _state.BlockStartUtc = null;
        _store.SaveState(_state);
        Changed?.Invoke();
    }

    /// <summary>
    /// Discard an idle span: bank the current block up to <paramref name="idleStartUtc"/>,
    /// then resume the same project from now — so the away time never reaches the log.
    /// </summary>
    public void DiscardIdleSince(DateTime idleStartUtc, string notes = "")
    {
        if (!IsRunning || Active is null) return;
        var project = Active;
        // Only truncate if the idle start is within the current block.
        if (_state.BlockStartUtc is DateTime start && idleStartUtc > start)
            BankCurrent(idleStartUtc, notes);
        // Resume fresh from now (BankCurrent cleared start).
        Active = project;
        _state.ActiveProjectCode = project.Code;
        _state.BlockStartUtc = _utcNow();
        _store.SaveState(_state);
        Changed?.Invoke();
    }

    /// <summary>Append the current block to the log, ending at <paramref name="endUtc"/>.</summary>
    private void BankCurrent(DateTime endUtc, string notes)
    {
        if (Active is null || _state.BlockStartUtc is not DateTime startUtc) return;
        if (endUtc < startUtc) endUtc = startUtc;

        var block = new TimeBlock
        {
            ProjectCode     = Active.Code,
            Asn             = Active.Asn,
            ProjectName     = Active.Name,
            StartLocal      = startUtc.ToLocalTime(),
            EndLocal        = endUtc.ToLocalTime(),
            DurationSeconds = (long)(endUtc - startUtc).TotalSeconds,
            Notes           = notes,
        };
        _state.BlockStartUtc = null;
        // Skip zero-length noise blocks (e.g. an immediate double switch).
        if (block.DurationSeconds > 0)
        {
            _log.Append(block);
            BlockLogged?.Invoke(block);
        }
    }

    // ---------- state helpers ----------

    public TrackerState State => _state;

    public void SavePillPosition(double left, double top)
    {
        _state.PillLeft = left;
        _state.PillTop = top;
        _store.SaveState(_state);
    }

    /// <summary>Today's tracked seconds per project code, including the live block.</summary>
    public Dictionary<string, long> TodaySecondsByProject()
    {
        var now = _utcNow().ToLocalTime();
        var totals = _log.SecondsByProjectOn(now);
        if (IsRunning && Active is not null)
        {
            totals.TryGetValue(Active.Code, out var cur);
            totals[Active.Code] = cur + CurrentElapsedSeconds;
        }
        return totals;
    }
}
