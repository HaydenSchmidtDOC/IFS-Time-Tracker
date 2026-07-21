using System.Globalization;

namespace TimeTracker.Core;

/// <summary>
/// Produces an IFS-shaped CSV from a month's internal log by applying the configurable
/// column mapping in <see cref="Settings.IfsExportMapping"/>. Refining the exact IFS
/// headers later is a settings edit — no code change.
/// </summary>
public sealed class IfsExporter
{
    private readonly CsvLog _log;
    private readonly AppPaths _paths;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public IfsExporter(CsvLog log, AppPaths paths)
    {
        _log = log;
        _paths = paths;
    }

    /// <summary>Write the mapped export for a month and return the file path.</summary>
    public string ExportMonth(int year, int month, Settings settings)
    {
        var blocks = _log.ReadMonth(year, month);
        var outFile = _paths.ExportFileFor(year, month);
        var mapping = settings.IfsExportMapping;

        using var w = new StreamWriter(outFile, append: false);
        w.WriteLine(Csv.Line(mapping.Select(m => m.Header)));
        foreach (var b in blocks)
            w.WriteLine(Csv.Line(mapping.Select(m => Resolve(m.Field, b, settings))));

        return outFile;
    }

    /// <summary>Resolve one mapped field token against a block. "=x" emits the literal x.</summary>
    private static string Resolve(string field, TimeBlock b, Settings settings)
    {
        if (field.StartsWith('=')) return field[1..];
        return field switch
        {
            "Date"            => b.StartLocal.ToString(settings.ExportDateFormat, Inv),
            "ProjectCode"     => b.ProjectCode,
            "Asn"             => b.Asn,
            "ProjectName"     => b.ProjectName,
            "StartLocal"      => b.StartLocal.ToString("yyyy-MM-dd HH:mm:ss", Inv),
            "EndLocal"        => b.EndLocal.ToString("yyyy-MM-dd HH:mm:ss", Inv),
            "DurationSeconds" => b.DurationSeconds.ToString(Inv),
            "DurationHours"   => b.DurationHours.ToString("0.0", Inv),
            "Notes"           => b.Notes,
            _                 => "" // unknown token -> empty, keeps export resilient
        };
    }
}
