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
