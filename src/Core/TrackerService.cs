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

    /// <summary>Blocks banked by an idle discard (see DiscardIdleSince) that are still "part of"
    /// the session currently running — the note-less first half of a split. If the CURRENT
    /// session eventually ends with a note (Stop/StartOrSwitch — see FinishCurrentBlock), that
    /// note is backfilled onto these too, so it covers the whole work session rather than just
    /// the part after you came back. In-memory only (not persisted): if the app restarts in
    /// between, the link is lost and these just keep their blank note, same as before this
    /// existed — a reasonable fallback for an edge case on top of an edge case.</summary>
    private readonly List<TimeBlock> _pendingNoteBlocks = new();

    public List<Project> Projects { get; private set; }

    /// <summary>The project currently being tracked, or null when stopped.</summary>
    public Project? Active { get; private set; }

    /// <summary>True while a block is running.</summary>
    public bool IsRunning => Active is not null && _state.BlockStartUtc is not null;

    /// <summary>Raised whenever the active project or running state changes.</summary>
    public event Action? Changed;

    /// <summary>Raised after a block is banked to the log (so the UI can refresh totals).</summary>
    public event Action<TimeBlock>? BlockLogged;

    /// <summary>
    /// Raised when a start/switch was refused because "now" already collides with an existing
    /// committed block — the UI shows an explanation instead of silently overlapping it. Not
    /// raised for the ordinary "already tracking this project, no-op" case.
    /// </summary>
    public event Action<TimeBlock>? StartRefused;

    /// <summary>
    /// Raised when a live session was stopped early because it grew into the start of an
    /// existing block placed ahead of it — the UI shows an explanation. This only ever fires from
    /// <see cref="CheckForLiveCollision"/>, which the UI calls once a second; TrackerService
    /// doesn't own a timer itself (matches Tick living in App, not here).
    /// </summary>
    public event Action<TimeBlock>? LiveSessionCollided;

    public TrackerService(JsonStore store, CsvLog log, Func<DateTime>? utcNow = null)
    {
        _store = store;
        _log = log;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);

        Projects = _store.LoadProjects();
        _state = _store.LoadState();

        // Resume a block that was live when the app last closed. Id takes priority — it survives
        // a rename — falling back to Code only for a state.json saved before ids existed.
        if (_state.BlockStartUtc is not null)
        {
            if (_state.ActiveProjectId is { } id) Active = FindById(id);
            if (Active is null && _state.ActiveProjectCode is { } code) Active = FindByCode(code);
        }
        if (Active is null) { _state.ActiveProjectId = null; _state.ActiveProjectCode = null; _state.BlockStartUtc = null; }
    }

    // ---------- projects ----------

    public Project? FindByCode(string code)
        => Projects.FirstOrDefault(p => string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase));

    public Project? FindById(string id)
        => Projects.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// Resolve the live project a historical block belongs to, preferring its stable
    /// <see cref="TimeBlock.ProjectId"/> (works even if Code/Name/Asn were edited since it was
    /// recorded) and falling back to <see cref="TimeBlock.ProjectCode"/> for rows logged before
    /// project ids existed. Null if the project has since been deleted.
    /// </summary>
    public Project? FindByBlock(TimeBlock b)
        => (!string.IsNullOrEmpty(b.ProjectId) ? FindById(b.ProjectId) : null) ?? FindByCode(b.ProjectCode);

    /// <summary>The last project that was tracked (for resuming after a stop), if it still exists.</summary>
    public Project? LastActive
        => (_state.LastActiveProjectId is { } id ? FindById(id) : null)
           ?? (_state.LastActiveProjectCode is { } c ? FindByCode(c) : null);

    /// <summary>Resume tracking the last project (used by the pill's Start toggle when stopped).</summary>
    public void ResumeLast()
    {
        var p = LastActive ?? Projects.FirstOrDefault();
        if (p is not null) StartOrSwitch(p);
    }

    public void AddProject(Project p)
    {
        if (string.IsNullOrEmpty(p.Id)) p.Id = Guid.NewGuid().ToString("N");
        p.Order = Projects.Count == 0 ? 0 : Projects.Max(x => x.Order) + 1;
        Projects.Add(p);
        _store.SaveProjects(Projects);
        Changed?.Invoke();
    }

    public void UpdateProjects(IEnumerable<Project> projects)
    {
        Projects = projects.ToList();
        foreach (var p in Projects)
            if (string.IsNullOrEmpty(p.Id)) p.Id = Guid.NewGuid().ToString("N");
        _store.SaveProjects(Projects);

        if (Active is not null)
        {
            // Re-resolve by Id rather than just checking existence — Active would otherwise keep
            // pointing at the pre-edit object (stale Code/Name/Asn/Color) until the next
            // StartOrSwitch, since the working copy edited in Settings is a set of clones, not
            // the same instances. If it's gone entirely, stop instead of tracking a ghost.
            var stillExists = FindById(Active.Id);
            if (stillExists is null) Stop();
            else Active = stillExists;
        }
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

    /// <summary>Begin tracking a project. Auto-banks any running block first (with optional
    /// note). Refuses (raising <see cref="StartRefused"/> instead) if "now" already falls inside
    /// an existing committed block for today — starting anyway would immediately overlap it.</summary>
    public void StartOrSwitch(Project project, string notes = "")
    {
        if (IsRunning && Active is not null && Active.Id == project.Id) return; // already live
        if (WouldCollideIfStartedNow() is TimeBlock collision) { StartRefused?.Invoke(collision); return; }
        if (IsRunning) FinishCurrentBlock(_utcNow(), notes);
        Active = project;
        _state.ActiveProjectId = project.Id;
        _state.ActiveProjectCode = project.Code;
        _state.LastActiveProjectId = project.Id;
        _state.LastActiveProjectCode = project.Code;
        _state.BlockStartUtc = _utcNow();
        _store.SaveState(_state);
        Changed?.Invoke();
    }

    /// <summary>The existing committed block that already covers this exact instant, if any —
    /// checked before starting/switching (see StartOrSwitch). Unscheduled ("by amount") entries
    /// never collide with anything, since they don't occupy a specific time (see
    /// TimeBlock.Unscheduled).</summary>
    private TimeBlock? WouldCollideIfStartedNow()
    {
        var nowLocal = _utcNow().ToLocalTime();
        return _log.ReadRange(nowLocal.Date, nowLocal.Date)
            .Where(b => !b.Unscheduled && b.StartLocal.Date == nowLocal.Date)
            .FirstOrDefault(b => nowLocal >= b.StartLocal && nowLocal < b.EndLocal);
    }

    /// <summary>
    /// Call once a second while a session is running (TrackerService doesn't own a timer itself
    /// — see App's Tick). If the live session has grown into the start of an existing block
    /// placed ahead of it, stops the session right there — truncated at that block's start, not
    /// past it, so nothing overlaps — and raises <see cref="LiveSessionCollided"/> with the block
    /// it hit so the UI can explain why. Returns the same block for convenience; null on the
    /// overwhelmingly common no-collision tick.
    /// </summary>
    public TimeBlock? CheckForLiveCollision()
    {
        if (!IsRunning || _state.BlockStartUtc is not DateTime startUtc) return null;
        var nowLocal = _utcNow().ToLocalTime();
        var startLocal = startUtc.ToLocalTime();

        var collision = _log.ReadRange(nowLocal.Date, nowLocal.Date)
            .Where(b => !b.Unscheduled && b.StartLocal.Date == nowLocal.Date && b.StartLocal > startLocal && b.StartLocal <= nowLocal)
            .OrderBy(b => b.StartLocal)
            .FirstOrDefault();
        if (collision is null) return null;

        // Same shutdown sequence Stop() uses, just ending at the collision's start instead of
        // "now" — truncating rather than silently running the banked block past it.
        var collisionStartUtc = DateTime.SpecifyKind(collision.StartLocal, DateTimeKind.Local).ToUniversalTime();
        FinishCurrentBlock(collisionStartUtc, "");
        Active = null;
        _state.ActiveProjectId = null;
        _state.ActiveProjectCode = null;
        _state.BlockStartUtc = null;
        _store.SaveState(_state);
        Changed?.Invoke();
        LiveSessionCollided?.Invoke(collision);
        return collision;
    }

    /// <summary>Stop tracking, banking the current block with an optional note.</summary>
    public void Stop(string notes = "")
    {
        if (IsRunning) FinishCurrentBlock(_utcNow(), notes);
        Active = null;
        _state.ActiveProjectId = null;
        _state.ActiveProjectCode = null;
        _state.BlockStartUtc = null;
        _store.SaveState(_state);
        Changed?.Invoke();
    }

    /// <summary>
    /// Discard an idle span: bank the current block up to <paramref name="idleStartUtc"/>,
    /// then resume the same project from now — so the away time never reaches the log. The
    /// banked (note-less) block is remembered — see _pendingNoteBlocks — so a note typed when
    /// this continued session eventually ends gets backfilled onto it too.
    /// </summary>
    public void DiscardIdleSince(DateTime idleStartUtc, string notes = "")
    {
        if (!IsRunning || Active is null) return;
        var project = Active;
        // Only truncate if the idle start is within the current block.
        if (_state.BlockStartUtc is DateTime start && idleStartUtc > start)
        {
            var banked = BankCurrent(idleStartUtc, notes);
            if (banked is not null) _pendingNoteBlocks.Add(banked);
        }
        // Resume fresh from now (BankCurrent cleared start).
        Active = project;
        _state.ActiveProjectId = project.Id;
        _state.ActiveProjectCode = project.Code;
        _state.BlockStartUtc = _utcNow();
        _store.SaveState(_state);
        Changed?.Invoke();
    }

    /// <summary>Ends the current block for real (Stop, StartOrSwitch, or the live-collision
    /// auto-stop) — as opposed to DiscardIdleSince's bank, which continues the same logical
    /// session under a new block. Banks with the given note, then — if a note was actually
    /// given — backfills that same note onto any earlier blocks this session was split from by
    /// an idle discard, so it reads as one note covering the whole work session rather than just
    /// the part after you came back. Clears that pending link regardless, since it's now
    /// resolved one way or the other and mustn't leak into whatever gets tracked next.</summary>
    private void FinishCurrentBlock(DateTime endUtc, string notes)
    {
        BankCurrent(endUtc, notes);
        if (!string.IsNullOrWhiteSpace(notes))
        {
            foreach (var pending in _pendingNoteBlocks)
            {
                var updated = new TimeBlock
                {
                    StartLocal = pending.StartLocal, EndLocal = pending.EndLocal,
                    Notes = notes, Unscheduled = pending.Unscheduled,
                };
                _log.UpdateBlock(pending, updated);
            }
        }
        _pendingNoteBlocks.Clear();
    }

    /// <summary>Append the current block to the log, ending at <paramref name="endUtc"/>. Returns
    /// the banked block, or null if it was zero-length and skipped (e.g. an immediate double
    /// switch) — see DiscardIdleSince, which needs the block itself, not just a side effect.</summary>
    private TimeBlock? BankCurrent(DateTime endUtc, string notes)
    {
        if (Active is null || _state.BlockStartUtc is not DateTime startUtc) return null;
        if (endUtc < startUtc) endUtc = startUtc;

        var block = new TimeBlock
        {
            Id              = Guid.NewGuid().ToString("N"),
            ProjectId       = Active.Id,
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
        if (block.DurationSeconds <= 0) return null;
        _log.Append(block);
        BlockLogged?.Invoke(block);
        return block;
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
