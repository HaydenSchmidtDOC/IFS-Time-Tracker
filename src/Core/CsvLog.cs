using System.Globalization;

namespace TimeTracker.Core;

/// <summary>
/// Appends completed <see cref="TimeBlock"/>s to a monthly CSV (<c>log-YYYY-MM.csv</c>)
/// and reads them back for "today" totals and month exports. This rich internal format
/// is the superset that <see cref="IfsExporter"/> maps down to IFS columns.
/// </summary>
public sealed class CsvLog
{
    private readonly AppPaths _paths;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Internal column order. Keep in sync with ParseRow / IfsExporter field tokens.
    // ProjectId/BlockId were appended after the original 9 columns (rather than inserted) so
    // every row ever written — including ones from before these columns existed — still parses
    // by the SAME fixed indices for Date..Notes; only the tail is optional. See ReadMonth.
    public static readonly string[] Header =
    {
        "Date", "ProjectCode", "ASN", "ProjectName",
        "StartLocal", "EndLocal", "DurationSeconds", "DurationHours", "Notes",
        "ProjectId", "BlockId", "Unscheduled", "SessionId",
    };

    /// <summary>Minimum column count for a row to be considered parseable at all — the original
    /// pre-id schema. Rows with exactly this many columns are legacy rows with no stored ids.</summary>
    private const int LegacyColumnCount = 9;

    public CsvLog(AppPaths paths) => _paths = paths;

    /// <summary>Append one block to the month file for its start date, writing a header if new.</summary>
    public void Append(TimeBlock b)
    {
        if (string.IsNullOrEmpty(b.Id)) b.Id = NewId();

        var file = _paths.LogFileFor(b.StartLocal);
        bool isNew = !File.Exists(file);
        Invalidate(file);
        using var w = new StreamWriter(file, append: true);
        if (isNew) w.WriteLine(Csv.Line(Header));
        w.WriteLine(RowFor(b));
    }

    private static string RowFor(TimeBlock b) => Csv.Line(new[]
    {
        b.StartLocal.ToString("yyyy-MM-dd", Inv),
        b.ProjectCode,
        b.Asn,
        b.ProjectName,
        b.StartLocal.ToString("yyyy-MM-dd HH:mm:ss", Inv),
        b.EndLocal.ToString("yyyy-MM-dd HH:mm:ss", Inv),
        b.DurationSeconds.ToString(Inv),
        b.DurationHours.ToString("0.0", Inv),
        b.Notes,
        b.ProjectId,
        b.Id,
        b.Unscheduled ? "1" : "0",
        b.SessionId,
    });

    /// <summary>Parsed month files, keyed by path and stamped with the file's last-write time and
    /// length. The UI asks for today's blocks every second, but this app is the only writer, so
    /// re-reading and re-parsing the file each time was pure waste (and tripped over transient file
    /// locks). A stat per call is enough to notice any change, including an outside edit or a
    /// OneDrive sync; our own writes also drop the entry explicitly.</summary>
    private readonly Dictionary<string, (DateTime Stamp, long Length, List<TimeBlock> Blocks)> _monthCache = new();
    private readonly object _cacheGate = new();

    private void Invalidate(string file) { lock (_cacheGate) _monthCache.Remove(file); }

    private static TimeBlock Copy(TimeBlock b) => new()
    {
        Id = b.Id, SessionId = b.SessionId, ProjectId = b.ProjectId, ProjectCode = b.ProjectCode, Asn = b.Asn, ProjectName = b.ProjectName,
        StartLocal = b.StartLocal, EndLocal = b.EndLocal, DurationSeconds = b.DurationSeconds,
        Notes = b.Notes, Unscheduled = b.Unscheduled,
    };

    /// <summary>Read every block recorded in the given month's file.</summary>
    public List<TimeBlock> ReadMonth(int year, int month)
    {
        var file = _paths.LogFileFor(new DateTime(year, month, 1));
        var info = new FileInfo(file);
        if (!info.Exists) { Invalidate(file); return new List<TimeBlock>(); }

        lock (_cacheGate)
        {
            if (!_monthCache.TryGetValue(file, out var hit) || hit.Stamp != info.LastWriteTimeUtc || hit.Length != info.Length)
            {
                var blocks = ParseMonth(file);
                if (blocks.Any(CrossesMidnight))
                {
                    SplitMidnightRows(file);
                    info = new FileInfo(file);
                    blocks = ParseMonth(file);
                }
                hit = (info.LastWriteTimeUtc, info.Length, blocks);
                _monthCache[file] = hit;
            }
            // Copies, so a caller editing a block can never corrupt the cache.
            return hit.Blocks.Select(Copy).ToList();
        }
    }

    /// <summary>True for a block that runs past the end of its start day (ending exactly at 00:00
    /// the next day is fine — it doesn't touch the new day).</summary>
    private static bool CrossesMidnight(TimeBlock b) => b.EndLocal > b.StartLocal.Date.AddDays(1);

    /// <summary>One-time migration of rows written before the tracker split blocks at midnight: each
    /// midnight-crossing row is cut into one row per day (first part keeps the original id, all keep
    /// project, flags and note) so per-day totals, the calendar and the editor see ordinary
    /// single-day blocks. Parts landing in a later month go to that month's file. The files touched
    /// are backed up once as *.pre-split.bak. Idempotent: afterwards nothing crosses midnight.</summary>
    private void SplitMidnightRows(string file)
    {
        BackupOnce(file);
        var lines = File.ReadAllLines(file).ToList();
        var output = new List<string>(lines.Count) { };
        var elsewhere = new List<TimeBlock>();

        for (int i = 0; i < lines.Count; i++)
        {
            var f = i == 0 || string.IsNullOrWhiteSpace(lines[i]) ? null : Csv.ParseLine(lines[i]);
            if (f is null || f.Count < LegacyColumnCount) { output.Add(lines[i]); continue; }

            var start = ParseDate(f[4]); var end = ParseDate(f[5]);
            if (end <= start.Date.AddDays(1)) { output.Add(lines[i]); continue; }

            bool first = true;
            var sessionId = f.Count > 12 && !string.IsNullOrEmpty(f[12]) ? f[12] : NewId();
            for (var segStart = start; segStart < end; segStart = segStart.Date.AddDays(1))
            {
                var segEnd = segStart.Date.AddDays(1) < end ? segStart.Date.AddDays(1) : end;
                var seg = new TimeBlock
                {
                    Id = first ? (f.Count > 10 && !string.IsNullOrEmpty(f[10]) ? f[10] : NewId()) : NewId(),
                    ProjectId = f.Count > 9 ? f[9] : "", ProjectCode = f[1], Asn = f[2], ProjectName = f[3],
                    StartLocal = segStart, EndLocal = segEnd,
                    DurationSeconds = (long)(segEnd - segStart).TotalSeconds,
                    Notes = f[8], Unscheduled = f.Count > 11 && f[11] == "1", SessionId = sessionId,
                };
                first = false;
                if (_paths.LogFileFor(segStart) == file) output.Add(RowFor(seg)); else elsewhere.Add(seg);
            }
        }

        // Later-month parts first: a crash in between then leaves duplicates (recoverable), not a loss.
        foreach (var seg in elsewhere) { BackupOnce(_paths.LogFileFor(seg.StartLocal)); Append(seg); }
        File.WriteAllLines(file, output);
        Invalidate(file);
    }

    private static void BackupOnce(string file)
    {
        var bak = file + ".pre-split.bak";
        if (File.Exists(file) && !File.Exists(bak)) File.Copy(file, bak);
    }

    private static List<TimeBlock> ParseMonth(string file)
    {
        var result = new List<TimeBlock>();
        foreach (var line in File.ReadLines(file).Skip(1)) // skip header
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var f = Csv.ParseLine(line);
            if (f.Count < LegacyColumnCount) continue;
            result.Add(new TimeBlock
            {
                ProjectCode     = f[1],
                Asn             = f[2],
                ProjectName     = f[3],
                StartLocal      = ParseDate(f[4]),
                EndLocal        = ParseDate(f[5]),
                DurationSeconds = long.TryParse(f[6], NumberStyles.Integer, Inv, out var s) ? s : 0,
                Notes           = f[8],
                ProjectId       = f.Count > 9 ? f[9] : "",
                Id              = f.Count > 10 && !string.IsNullOrEmpty(f[10]) ? f[10] : LegacyId(f),
                Unscheduled     = f.Count > 11 && f[11] == "1",
                SessionId       = f.Count > 12 ? f[12] : "",
            });
        }
        return result;
    }

    /// <summary>
    /// Read every block whose start date falls within [from, toInclusive] — may span month
    /// files (e.g. a week straddling a month boundary), so this reads each touched month once.
    /// </summary>
    public List<TimeBlock> ReadRange(DateTime from, DateTime toInclusive)
    {
        var result = new List<TimeBlock>();
        var cursor = new DateTime(from.Year, from.Month, 1);
        var lastMonth = new DateTime(toInclusive.Year, toInclusive.Month, 1);
        while (cursor <= lastMonth)
        {
            foreach (var b in ReadMonth(cursor.Year, cursor.Month))
                if (b.StartLocal.Date >= from.Date && b.StartLocal.Date <= toInclusive.Date)
                    result.Add(b);
            cursor = cursor.AddMonths(1);
        }
        return result;
    }

    /// <summary>
    /// Remove a recorded block — all of its session's parts if it was split at midnight (see
    /// <see cref="TimeBlock.SessionId"/>). Matched by <see cref="TimeBlock.Id"/> when the target has
    /// one and the file row does too; otherwise (either side is a legacy row with no id) falls
    /// back to project code + exact start/end timestamps, as before ids existed. Returns true if
    /// a matching row was found and removed.
    /// </summary>
    public bool DeleteBlock(TimeBlock target)
    {
        if (string.IsNullOrEmpty(target.SessionId)) return DeleteRow(target);
        bool any = false;
        foreach (var part in ReadSessionParts(target)) any |= DeleteRow(part);
        return any;
    }

    private bool DeleteRow(TimeBlock target)
    {
        var file = _paths.LogFileFor(target.StartLocal);
        if (!File.Exists(file)) return false;

        var lines = File.ReadAllLines(file).ToList();
        for (int i = 1; i < lines.Count; i++) // skip header
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var f = Csv.ParseLine(lines[i]);
            if (f.Count < LegacyColumnCount) continue;

            if (!RowMatches(f, target)) continue;

            lines.RemoveAt(i);
            File.WriteAllLines(file, lines);
            Invalidate(file);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Rewrite a block's start/end/duration/notes in place — the fields a drag-to-edit can
    /// actually change — leaving its project snapshot (code/ASN/name) and <see
    /// cref="TimeBlock.Id"/> untouched. <paramref name="original"/> identifies which row to
    /// rewrite (same matching rule as <see cref="DeleteBlock"/>); <paramref name="updated"/>
    /// carries the new Start/End/Notes to write. Both must share the same StartLocal.Date's month
    /// file — a drag that would move a block across midnight into a different month file is not
    /// supported here and must be prevented by the caller.
    ///
    /// <paramref name="updated"/>.ProjectId is the signal for whether the project itself is also
    /// being reassigned (as the manual-edit dialog allows, unlike a drag): empty means "keep the
    /// row's existing project snapshot" (every drag call site leaves it at TimeBlock's default),
    /// non-empty means "overwrite the snapshot columns with updated's project fields too".
    ///
    /// Returns true if a matching row was found and rewritten.
    /// </summary>
    public bool UpdateBlock(TimeBlock original, TimeBlock updated)
    {
        var file = _paths.LogFileFor(original.StartLocal);
        if (!File.Exists(file)) return false;

        var lines = File.ReadAllLines(file).ToList();
        for (int i = 1; i < lines.Count; i++) // skip header
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var f = Csv.ParseLine(lines[i]);
            if (f.Count < LegacyColumnCount) continue;
            if (!RowMatches(f, original)) continue;

            bool projectChanged = !string.IsNullOrEmpty(updated.ProjectId);
            string projectId = projectChanged ? updated.ProjectId : (f.Count > 9 ? f[9] : "");
            string code = projectChanged ? updated.ProjectCode : f[1];
            string asn = projectChanged ? updated.Asn : f[2];
            string name = projectChanged ? updated.ProjectName : f[3];
            string blockId = f.Count > 10 && !string.IsNullOrEmpty(f[10]) ? f[10] : NewId();
            long durationSeconds = Math.Max(0, (long)(updated.EndLocal - updated.StartLocal).TotalSeconds);

            lines[i] = Csv.Line(new[]
            {
                updated.StartLocal.ToString("yyyy-MM-dd", Inv),
                code, asn, name,
                updated.StartLocal.ToString("yyyy-MM-dd HH:mm:ss", Inv),
                updated.EndLocal.ToString("yyyy-MM-dd HH:mm:ss", Inv),
                durationSeconds.ToString(Inv),
                Rounding.ToTenthHours(durationSeconds).ToString("0.0", Inv),
                updated.Notes ?? f[8],
                projectId,
                blockId,
                updated.Unscheduled ? "1" : "0",
                f.Count > 12 ? f[12] : "",
            });
            File.WriteAllLines(file, lines);
            Invalidate(file);
            return true;
        }
        return false;
    }

    /// <summary>Every row of the session <paramref name="part"/> belongs to, in time order — just the
    /// block itself if it was never split. A session is contiguous, so its parts sit in the month of
    /// the given part or the one either side.</summary>
    public List<TimeBlock> ReadSessionParts(TimeBlock part)
    {
        if (string.IsNullOrEmpty(part.SessionId)) return new() { part };
        var parts = new List<TimeBlock>();
        var month = new DateTime(part.StartLocal.Year, part.StartLocal.Month, 1);
        for (var m = month.AddMonths(-1); m <= month.AddMonths(1); m = m.AddMonths(1))
            parts.AddRange(ReadMonth(m.Year, m.Month).Where(b => b.SessionId == part.SessionId));
        return parts.Count == 0 ? new() { part } : parts.OrderBy(b => b.StartLocal).ToList();
    }

    /// <summary>One block spanning a whole split session (first part's start to last part's end, notes
    /// from the first part that has one) for editing it as a unit; the block itself if not split.</summary>
    public TimeBlock MergedSession(TimeBlock part)
    {
        var parts = ReadSessionParts(part);
        if (parts.Count <= 1) return part;
        var merged = Copy(parts[0]);
        merged.EndLocal = parts[^1].EndLocal;
        merged.DurationSeconds = parts.Sum(p => p.DurationSeconds);
        merged.Notes = parts.Select(p => p.Notes).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? "";
        return merged;
    }

    /// <summary>Applies an edit made to a (possibly merged) block to its whole session: the old rows
    /// are replaced by one row per day of <paramref name="updated"/>'s span, sharing a SessionId when
    /// there is more than one. A plain single-day block with a single-day result is just
    /// <see cref="UpdateBlock"/>. Returns true if the original was found.</summary>
    public bool UpdateSession(TimeBlock original, TimeBlock updated)
    {
        var parts = ReadSessionParts(original);
        bool multi = !updated.Unscheduled && updated.EndLocal > updated.StartLocal.Date.AddDays(1);
        if (parts.Count <= 1 && !multi) return UpdateBlock(parts[0], updated);

        bool found = false;
        foreach (var part in parts) found |= DeleteRow(part);
        if (!found) return false;

        var sessionId = multi ? (parts.Count > 1 ? original.SessionId : NewId()) : "";
        bool first = true;
        for (var s = updated.StartLocal; s < updated.EndLocal; s = s.Date.AddDays(1))
        {
            var e = multi && s.Date.AddDays(1) < updated.EndLocal ? s.Date.AddDays(1) : updated.EndLocal;
            var piece = Copy(updated);
            piece.Id = first ? parts[0].Id : NewId();
            piece.SessionId = sessionId;
            piece.StartLocal = s; piece.EndLocal = e;
            piece.DurationSeconds = Math.Max(0, (long)(e - s).TotalSeconds);
            Append(piece);
            first = false;
            if (!multi) break;
        }
        return true;
    }

    /// <summary>Sum tracked seconds per project code for a given local date (for "Today").</summary>
    public Dictionary<string, long> SecondsByProjectOn(DateTime localDate)
    {
        var totals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in ReadMonth(localDate.Year, localDate.Month))
        {
            if (b.StartLocal.Date != localDate.Date) continue;
            totals.TryGetValue(b.ProjectCode, out var cur);
            totals[b.ProjectCode] = cur + b.DurationSeconds;
        }
        return totals;
    }

    /// <summary>True when a parsed CSV row identifies the same block as <paramref name="target"/>.</summary>
    private static bool RowMatches(List<string> f, TimeBlock target)
    {
        string rowId = f.Count > 10 ? f[10] : "";
        if (!string.IsNullOrEmpty(target.Id) && !string.IsNullOrEmpty(rowId))
            return string.Equals(rowId, target.Id, StringComparison.Ordinal);

        return string.Equals(f[1], target.ProjectCode, StringComparison.OrdinalIgnoreCase)
            && ParseDate(f[4]) == target.StartLocal && ParseDate(f[5]) == target.EndLocal;
    }

    /// <summary>
    /// Deterministic stand-in id for a row that predates <see cref="TimeBlock.Id"/> — built from
    /// its raw (unparsed) code/start/end text so it's stable across reads without needing to
    /// actually persist anything. Prefixed so it's recognisably synthetic, not a real stored id.
    /// </summary>
    private static string LegacyId(List<string> f) => $"legacy:{f[1]}|{f[4]}|{f[5]}";

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static DateTime ParseDate(string s)
        => DateTime.TryParse(s, Inv, DateTimeStyles.None, out var d) ? d : default;
}
