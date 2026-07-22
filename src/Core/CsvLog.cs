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
    public static readonly string[] Header =
    {
        "Date", "ProjectCode", "ASN", "ProjectName",
        "StartLocal", "EndLocal", "DurationSeconds", "DurationHours", "Notes"
    };

    public CsvLog(AppPaths paths) => _paths = paths;

    /// <summary>Append one block to the month file for its start date, writing a header if new.</summary>
    public void Append(TimeBlock b)
    {
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
            b.Notes
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
            if (f.Count < Header.Length) continue;
            result.Add(new TimeBlock
            {
                ProjectCode     = f[1],
                Asn             = f[2],
                ProjectName     = f[3],
                StartLocal      = ParseDate(f[4]),
                EndLocal        = ParseDate(f[5]),
                DurationSeconds = long.TryParse(f[6], NumberStyles.Integer, Inv, out var s) ? s : 0,
                Notes           = f[8],
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
    /// Remove a single recorded block, matched by project code + exact start/end timestamps
    /// (blocks have no stored id; this triple is unique in practice since the tracker always
    /// advances time between blocks). Returns true if a matching row was found and removed.
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
            if (f.Count < Header.Length) continue;

            if (string.Equals(f[1], target.ProjectCode, StringComparison.OrdinalIgnoreCase)
                && ParseDate(f[4]) == target.StartLocal && ParseDate(f[5]) == target.EndLocal)
            {
                lines.RemoveAt(i);
                File.WriteAllLines(file, lines);
                return true;
            }
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

    private static DateTime ParseDate(string s)
        => DateTime.TryParse(s, Inv, DateTimeStyles.None, out var d) ? d : default;
}
