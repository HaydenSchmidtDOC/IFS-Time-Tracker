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
        "ProjectId", "BlockId",
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
        using var w = new StreamWriter(file, append: true);
        if (isNew) w.WriteLine(Csv.Line(Header));
        w.WriteLine(Csv.Line(new[]
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
        }));
    }

    /// <summary>Read every block recorded in the given month's file.</summary>
    public List<TimeBlock> ReadMonth(int year, int month)
    {
        var file = _paths.LogFileFor(new DateTime(year, month, 1));
        var result = new List<TimeBlock>();
        if (!File.Exists(file)) return result;

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
    /// Remove a single recorded block. Matched by <see cref="TimeBlock.Id"/> when the target has
    /// one and the file row does too; otherwise (either side is a legacy row with no id) falls
    /// back to project code + exact start/end timestamps, as before ids existed. Returns true if
    /// a matching row was found and removed.
    /// </summary>
    public bool DeleteBlock(TimeBlock target)
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
            });
            File.WriteAllLines(file, lines);
            return true;
        }
        return false;
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
