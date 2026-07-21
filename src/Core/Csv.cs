using System.Text;

namespace TimeTracker.Core;

/// <summary>Minimal RFC-4180 CSV field encoding/decoding — no external dependency.</summary>
public static class Csv
{
    /// <summary>Escape a single field, quoting when it contains a comma, quote, or newline.</summary>
    public static string Escape(string? field)
    {
        field ??= "";
        bool mustQuote = field.Contains(',') || field.Contains('"')
            || field.Contains('\n') || field.Contains('\r');
        if (!mustQuote) return field;
        return "\"" + field.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>Join fields into one CSV line.</summary>
    public static string Line(IEnumerable<string?> fields)
        => string.Join(",", fields.Select(Escape));

    /// <summary>Parse a single CSV line into its fields (handles quotes/escapes).</summary>
    public static List<string> ParseLine(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == ',') { result.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
        }
        result.Add(sb.ToString());
        return result;
    }
}
