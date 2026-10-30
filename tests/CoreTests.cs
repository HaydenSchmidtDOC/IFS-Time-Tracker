using TimeTracker.Core;
using Xunit;

namespace TimeTracker.Tests;

public class RoundingTests
{
    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(120, 0.0)]    // 2 min  -> 0.033 -> 0.0
    [InlineData(179, 0.0)]    // 2m59s  -> 0.0497 -> 0.0
    [InlineData(180, 0.1)]    // 3 min  -> 0.05 tie -> rounds up
    [InlineData(240, 0.1)]    // 4 min  -> 0.067 -> 0.1
    [InlineData(360, 0.1)]    // 6 min  -> exactly 0.1
    [InlineData(3600, 1.0)]   // 1 hour
    [InlineData(8677, 2.4)]   // 2h24m37s -> 2.41 -> 2.4
    public void RoundsToNearestTenthHour_HalvesUp(long seconds, double expected)
        => Assert.Equal(expected, Rounding.ToTenthHours(seconds), 3);
}

public class TrackerServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly AppPaths _paths;
    private readonly JsonStore _store;
    private readonly CsvLog _log;
    private DateTime _now;

    public TrackerServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tt-tests-" + Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_dir);
        _store = new JsonStore(_paths);
        _log = new CsvLog(_paths);
        _now = new DateTime(2026, 7, 21, 8, 0, 0, DateTimeKind.Utc);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private TrackerService NewService() => new(_store, _log, () => _now);

    private static Project P(string code, string asn) => new() { Code = code, Asn = asn, Name = code };

    [Fact]
    public void SwitchingBanksPriorBlock_AndStartsNew()
    {
        var svc = NewService();
        var a = P("BRIDGE-42", "100234");
        var b = P("DOCK-17", "100199");
        svc.AddProject(a); svc.AddProject(b);

        svc.StartOrSwitch(a);
        _now = _now.AddHours(2).AddMinutes(24).AddSeconds(37); // 2h24m37s on A
        svc.StartOrSwitch(b);                                   // banks A, starts B
        _now = _now.AddMinutes(66);                             // 1h06m on B
        svc.Stop();                                             // banks B

        var blocks = _log.ReadMonth(2026, 7);
        Assert.Equal(2, blocks.Count);

        Assert.Equal("BRIDGE-42", blocks[0].ProjectCode);
        Assert.Equal(8677, blocks[0].DurationSeconds);
        Assert.Equal(2.4, blocks[0].DurationHours, 3);

        Assert.Equal("DOCK-17", blocks[1].ProjectCode);
        Assert.Equal(3960, blocks[1].DurationSeconds);
        Assert.Equal(1.1, blocks[1].DurationHours, 3);

        Assert.False(svc.IsRunning);
    }

    [Fact]
    public void RestartingSameProject_IsNoOp()
    {
        var svc = NewService();
        var a = P("ADMIN", "100001");
        svc.AddProject(a);
        svc.StartOrSwitch(a);
        _now = _now.AddMinutes(30);
        svc.StartOrSwitch(a); // same project -> should not bank anything

        Assert.Empty(_log.ReadMonth(2026, 7));
        Assert.True(svc.IsRunning);
        Assert.Equal(1800, svc.CurrentElapsedSeconds);
    }

    [Fact]
    public void ZeroLengthBlock_IsNotLogged()
    {
        var svc = NewService();
        var a = P("A", "1"); var b = P("B", "2");
        svc.AddProject(a); svc.AddProject(b);
        svc.StartOrSwitch(a);
        svc.StartOrSwitch(b); // immediate double switch, no time elapsed
        Assert.Empty(_log.ReadMonth(2026, 7));
    }

    [Fact]
    public void DiscardIdle_TruncatesToIdleStart_AndResumes()
    {
        var svc = NewService();
        var a = P("CONV-CAL", "100320");
        svc.AddProject(a);

        var start = _now;
        svc.StartOrSwitch(a);
        _now = _now.AddMinutes(10);
        var idleStart = _now;          // active for 10 min, then went idle
        _now = _now.AddMinutes(24);    // 24 min away
        svc.DiscardIdleSince(idleStart);

        var blocks = _log.ReadMonth(2026, 7);
        Assert.Single(blocks);
        Assert.Equal(600, blocks[0].DurationSeconds); // only the 10 worked minutes kept
        Assert.True(svc.IsRunning);                    // resumed same project
        Assert.Equal("CONV-CAL", svc.Active!.Code);
        Assert.Equal(0, svc.CurrentElapsedSeconds);    // fresh block from now
    }

    [Fact]
    public void LiveBlockCrossingMidnight_IsSplitPerDay_AndFinalNoteCoversAllRows()
    {
        var svc = NewService();
        var a = P("CONV-CAL", "100320");
        svc.AddProject(a);

        _now = new DateTime(2026, 7, 21, 0, 0, 0, DateTimeKind.Local).AddHours(23).ToUniversalTime();
        svc.StartOrSwitch(a);
        _now = _now.AddHours(2);          // 01:00 next day
        svc.RollOverMidnight();           // tick after midnight
        Assert.True(svc.IsRunning);
        _now = _now.AddHours(1);          // 02:00
        svc.Stop("overnight run");

        var blocks = _log.ReadMonth(2026, 7).OrderBy(b => b.StartLocal).ToList();
        Assert.Equal(2, blocks.Count);
        Assert.Equal(new DateTime(2026, 7, 22), blocks[0].EndLocal);
        Assert.Equal(blocks[0].EndLocal, blocks[1].StartLocal);
        Assert.Equal(3600, blocks[0].DurationSeconds);
        Assert.Equal(7200, blocks[1].DurationSeconds);
        Assert.All(blocks, b => Assert.Equal("overnight run", b.Notes));
        Assert.False(string.IsNullOrEmpty(blocks[0].SessionId));
        Assert.Equal(blocks[0].SessionId, blocks[1].SessionId);
    }

    [Fact]
    public void StopAfterMidnightWithoutTick_StillSplitsAtMidnight()
    {
        var svc = NewService();
        var a = P("X", "1");
        svc.AddProject(a);
        _now = new DateTime(2026, 7, 21, 23, 0, 0, DateTimeKind.Local).ToUniversalTime();
        svc.StartOrSwitch(a);
        _now = _now.AddHours(3);
        svc.Stop();
        Assert.Equal(2, _log.ReadMonth(2026, 7).Count);
    }

    [Fact]
    public void NoteOnStop_BackfillsOntoBlocksSplitByEarlierIdleDiscards()
    {
        var svc = NewService();
        var a = P("CONV-CAL", "100320");
        svc.AddProject(a);

        svc.StartOrSwitch(a);
        _now = _now.AddMinutes(10);
        var idle1 = _now;
        _now = _now.AddMinutes(20);
        svc.DiscardIdleSince(idle1);   // split #1 (10 min), no note

        _now = _now.AddMinutes(15);
        var idle2 = _now;
        _now = _now.AddMinutes(5);
        svc.DiscardIdleSince(idle2);   // split #2 (15 min), no note — a SECOND break

        _now = _now.AddMinutes(20);    // final stretch (20 min)
        svc.Stop("wrapped up the conveyor calibration");

        var blocks = _log.ReadMonth(2026, 7).OrderBy(b => b.StartLocal).ToList();
        Assert.Equal(3, blocks.Count);
        Assert.All(blocks, b => Assert.Equal("wrapped up the conveyor calibration", b.Notes));
        Assert.Equal(600, blocks[0].DurationSeconds);  // 10 min
        Assert.Equal(900, blocks[1].DurationSeconds);  // 15 min
        Assert.Equal(1200, blocks[2].DurationSeconds); // 20 min
    }

    [Fact]
    public void NoteBackfill_DoesNotLeakIntoTheNextUnrelatedSession()
    {
        var svc = NewService();
        var a = P("CONV-CAL", "100320");
        var b = P("ADMIN", "100001");
        svc.AddProject(a); svc.AddProject(b);

        svc.StartOrSwitch(a);
        _now = _now.AddMinutes(10);
        var idle1 = _now;
        _now = _now.AddMinutes(20);
        svc.DiscardIdleSince(idle1);   // split, no note

        _now = _now.AddMinutes(10);
        svc.Stop();                    // ends with NO note — should just clear the pending link

        svc.StartOrSwitch(b);
        _now = _now.AddMinutes(30);
        svc.Stop("unrelated admin work");

        var blocks = _log.ReadMonth(2026, 7).OrderBy(b => b.StartLocal).ToList();
        Assert.Equal(3, blocks.Count);
        Assert.Equal("", blocks[0].Notes);                    // the earlier split stays note-less
        Assert.Equal("", blocks[1].Notes);                    // the no-note stop stays note-less
        Assert.Equal("unrelated admin work", blocks[2].Notes); // only the actually-noted block gets it
    }

    [Fact]
    public void TodayTotals_IncludeLiveBlock()
    {
        var svc = NewService();
        var a = P("A", "1");
        svc.AddProject(a);
        svc.StartOrSwitch(a);
        _now = _now.AddMinutes(15);

        var totals = svc.TodaySecondsByProject();
        Assert.Equal(900, totals["A"]);
    }
}

public class IfsExporterTests : IDisposable
{
    private readonly string _dir;
    private readonly AppPaths _paths;
    private readonly CsvLog _log;

    public IfsExporterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tt-export-tests-" + Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_dir);
        _log = new CsvLog(_paths);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void ExportsColumnsInMappingOrder_WithLiterals()
    {
        _log.Append(new TimeBlock
        {
            ProjectCode = "BRIDGE-42", Asn = "100234", ProjectName = "Bridge conveyor",
            StartLocal = new DateTime(2026, 7, 21, 8, 0, 0),
            EndLocal = new DateTime(2026, 7, 21, 10, 24, 0),
            DurationSeconds = 8640, Notes = "aligned rollers",
        });

        var settings = new Settings
        {
            IfsExportMapping = new()
            {
                new() { Header = "Source",  Field = "=TIMETRACKER" }, // literal
                new() { Header = "Code",    Field = "ProjectCode" },
                new() { Header = "Hrs",     Field = "DurationHours" },
                new() { Header = "When",    Field = "Date" },
            },
            ExportDateFormat = "dd/MM/yyyy",
        };

        var exporter = new IfsExporter(_log, _paths);
        var file = exporter.ExportMonth(2026, 7, settings);
        var lines = File.ReadAllLines(file);

        Assert.Equal("Source,Code,Hrs,When", lines[0]);
        Assert.Equal("TIMETRACKER,BRIDGE-42,2.4,21/07/2026", lines[1]);
    }

    [Fact]
    public void ReadRange_SpansTwoMonthFiles_AndExcludesOutOfRangeDays()
    {
        TimeBlock On(DateTime d) => new()
        {
            ProjectCode = "A", Asn = "1", StartLocal = d, EndLocal = d.AddHours(1), DurationSeconds = 3600,
        };
        _log.Append(On(new DateTime(2026, 7, 30))); // in range (Jul, week start)
        _log.Append(On(new DateTime(2026, 7, 31))); // in range (Jul, last day)
        _log.Append(On(new DateTime(2026, 8, 1)));  // in range (Aug, week continues)
        _log.Append(On(new DateTime(2026, 8, 2)));  // in range (Aug, week end)
        _log.Append(On(new DateTime(2026, 7, 29))); // out of range — day before the week
        _log.Append(On(new DateTime(2026, 8, 3)));  // out of range — day after the week

        var blocks = _log.ReadRange(new DateTime(2026, 7, 30), new DateTime(2026, 8, 2));

        Assert.Equal(4, blocks.Count);
        Assert.All(blocks, b => Assert.InRange(b.StartLocal.Date, new DateTime(2026, 7, 30), new DateTime(2026, 8, 2)));
    }

    [Fact]
    public void DeleteBlock_RemovesOnlyTheMatchingRow()
    {
        var keep1 = new TimeBlock { ProjectCode = "A", StartLocal = new DateTime(2026, 7, 21, 8, 0, 0), EndLocal = new DateTime(2026, 7, 21, 9, 0, 0), DurationSeconds = 3600 };
        var target = new TimeBlock { ProjectCode = "B", StartLocal = new DateTime(2026, 7, 21, 9, 0, 0), EndLocal = new DateTime(2026, 7, 21, 9, 30, 0), DurationSeconds = 1800 };
        var keep2 = new TimeBlock { ProjectCode = "A", StartLocal = new DateTime(2026, 7, 21, 9, 30, 0), EndLocal = new DateTime(2026, 7, 21, 10, 0, 0), DurationSeconds = 1800 };
        _log.Append(keep1); _log.Append(target); _log.Append(keep2);

        bool removed = _log.DeleteBlock(target);

        Assert.True(removed);
        var remaining = _log.ReadMonth(2026, 7);
        Assert.Equal(2, remaining.Count);
        Assert.DoesNotContain(remaining, b => b.ProjectCode == "B");
    }

    [Fact]
    public void DeleteBlock_NoMatch_ReturnsFalseAndLeavesFileUntouched()
    {
        var b = new TimeBlock { ProjectCode = "A", StartLocal = new DateTime(2026, 7, 21, 8, 0, 0), EndLocal = new DateTime(2026, 7, 21, 9, 0, 0), DurationSeconds = 3600 };
        _log.Append(b);

        var notThere = new TimeBlock { ProjectCode = "A", StartLocal = new DateTime(2026, 7, 21, 10, 0, 0), EndLocal = new DateTime(2026, 7, 21, 11, 0, 0) };
        bool removed = _log.DeleteBlock(notThere);

        Assert.False(removed);
        Assert.Single(_log.ReadMonth(2026, 7));
    }
}

public class CsvTests
{
    [Fact]
    public void EscapesCommasQuotesNewlines()
    {
        Assert.Equal("plain", Csv.Escape("plain"));
        Assert.Equal("\"a,b\"", Csv.Escape("a,b"));
        Assert.Equal("\"say \"\"hi\"\"\"", Csv.Escape("say \"hi\""));
    }

    [Fact]
    public void RoundTripsQuotedFields()
    {
        var line = Csv.Line(new[] { "BRIDGE-42", "note, with comma", "and \"quotes\"" });
        var parsed = Csv.ParseLine(line);
        Assert.Equal(new[] { "BRIDGE-42", "note, with comma", "and \"quotes\"" }, parsed);
    }
}

public class MidnightMigrationTests : IDisposable
{
    private readonly string _dir;
    private readonly AppPaths _paths;
    private readonly CsvLog _log;

    public MidnightMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tt-tests-" + Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_dir);
        _log = new CsvLog(_paths);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static TimeBlock B(DateTime s, DateTime e, string note = "n") => new()
    {
        ProjectCode = "P", ProjectName = "P", Asn = "1", ProjectId = "pid", StartLocal = s, EndLocal = e,
        DurationSeconds = (long)(e - s).TotalSeconds, Notes = note,
    };

    [Fact]
    public void OldMidnightCrossingRow_IsSplitPerDay_WithBackup_AndIdempotent()
    {
        _log.Append(B(new DateTime(2026, 7, 21, 22, 0, 0), new DateTime(2026, 7, 23, 1, 0, 0), "late"));
        _log.Append(B(new DateTime(2026, 7, 24, 9, 0, 0), new DateTime(2026, 7, 24, 10, 0, 0)));
        // append bypasses ReadMonth, so the file is still the old style until it's read
        var blocks = _log.ReadMonth(2026, 7).OrderBy(b => b.StartLocal).ToList();

        Assert.Equal(4, blocks.Count);
        Assert.Equal(new[] { 7200L, 86400L, 3600L, 3600L }, blocks.Select(b => b.DurationSeconds).ToArray());
        Assert.All(blocks.Take(3), b => Assert.Equal("late", b.Notes));
        Assert.All(blocks, b => Assert.True(b.EndLocal <= b.StartLocal.Date.AddDays(1)));
        Assert.True(File.Exists(_paths.LogFileFor(new DateTime(2026, 7, 1)) + ".pre-split.bak"));
        Assert.Equal(4, new CsvLog(_paths).ReadMonth(2026, 7).Count); // re-read: no further change
    }

    [Fact]
    public void SplitParts_ShareSessionId_AndEditingTreatsThemAsOne()
    {
        _log.Append(B(new DateTime(2026, 7, 21, 22, 0, 0), new DateTime(2026, 7, 22, 1, 0, 0), "late"));
        var parts = _log.ReadMonth(2026, 7).OrderBy(b => b.StartLocal).ToList();
        Assert.Equal(2, parts.Count);
        Assert.False(string.IsNullOrEmpty(parts[0].SessionId));
        Assert.Equal(parts[0].SessionId, parts[1].SessionId);
        Assert.NotEqual(parts[0].Id, parts[1].Id);

        var merged = _log.MergedSession(parts[1]);   // editing from either half
        Assert.Equal(new DateTime(2026, 7, 21, 22, 0, 0), merged.StartLocal);
        Assert.Equal(new DateTime(2026, 7, 22, 1, 0, 0), merged.EndLocal);
        Assert.Equal(10800, merged.DurationSeconds);

        // extend the end to 02:30 and change the note: both days update, still one session
        merged.EndLocal = new DateTime(2026, 7, 22, 2, 30, 0);
        merged.DurationSeconds = (long)(merged.EndLocal - merged.StartLocal).TotalSeconds;
        merged.Notes = "edited";
        Assert.True(_log.UpdateSession(parts[1], merged));
        var after = _log.ReadMonth(2026, 7).OrderBy(b => b.StartLocal).ToList();
        Assert.Equal(new[] { 7200L, 9000L }, after.Select(b => b.DurationSeconds).ToArray());
        Assert.All(after, b => Assert.Equal("edited", b.Notes));
        Assert.Equal(after[0].SessionId, after[1].SessionId);

        // pull it back inside one day: collapses to an ordinary single block
        merged = _log.MergedSession(after[0]);
        merged.EndLocal = new DateTime(2026, 7, 21, 23, 30, 0);
        Assert.True(_log.UpdateSession(after[0], merged));
        var single = Assert.Single(_log.ReadMonth(2026, 7));
        Assert.Equal("", single.SessionId);
        Assert.Equal(5400, single.DurationSeconds);
    }

    [Fact]
    public void DeletingOnePart_DeletesTheWholeSession()
    {
        _log.Append(B(new DateTime(2026, 7, 21, 22, 0, 0), new DateTime(2026, 7, 22, 1, 0, 0)));
        _log.Append(B(new DateTime(2026, 7, 25, 9, 0, 0), new DateTime(2026, 7, 25, 10, 0, 0)));
        var parts = _log.ReadMonth(2026, 7).OrderBy(b => b.StartLocal).ToList();
        Assert.True(_log.DeleteBlock(parts[1]));
        Assert.Single(_log.ReadMonth(2026, 7));
    }

    [Fact]
    public void CrossingMonthEnd_MovesSecondPartIntoNextMonthFile()
    {
        _log.Append(B(new DateTime(2026, 7, 31, 23, 0, 0), new DateTime(2026, 8, 1, 2, 0, 0)));
        var july = _log.ReadMonth(2026, 7);
        var august = _log.ReadMonth(2026, 8);
        Assert.Equal(3600, Assert.Single(july).DurationSeconds);
        Assert.Equal(7200, Assert.Single(august).DurationSeconds);
    }
}

public class CsvLogCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tt-cache-tests-" + Guid.NewGuid().ToString("N"));
    private readonly CsvLog _log;

    public CsvLogCacheTests() => _log = new CsvLog(new AppPaths(_dir));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static TimeBlock Block(int hour) => new()
    {
        ProjectCode = "P", StartLocal = new DateTime(2026, 7, 21, hour, 0, 0),
        EndLocal = new DateTime(2026, 7, 21, hour + 1, 0, 0), DurationSeconds = 3600,
    };

    [Fact]
    public void ReadMonth_SeesOwnWritesAndOutsideEdits_AndCallerMutationsDontLeak()
    {
        _log.Append(Block(8));
        Assert.Single(_log.ReadMonth(2026, 7));

        _log.Append(Block(10));
        var blocks = _log.ReadMonth(2026, 7);
        Assert.Equal(2, blocks.Count);

        blocks[0].Notes = "mutated";
        Assert.Equal("", _log.ReadMonth(2026, 7)[0].Notes);

        var file = Directory.GetFiles(_dir, "log-2026-07*.csv").Single();
        File.AppendAllText(file, File.ReadAllLines(file)[1] + Environment.NewLine);
        Assert.Equal(3, _log.ReadMonth(2026, 7).Count);

        Assert.True(_log.DeleteBlock(blocks[1]));
        Assert.Equal(2, _log.ReadMonth(2026, 7).Count);
    }
}
