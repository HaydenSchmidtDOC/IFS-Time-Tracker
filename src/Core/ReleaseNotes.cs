using System.Text;

namespace TimeTracker.Core;

/// <summary>Pulls one version's section out of CHANGELOG.md as plain text for the "what's new" popup.</summary>
public static class ReleaseNotes
{
    /// <summary>Text of the "## [version]" section — headings as lines, entries as "• " bullets,
    /// markdown emphasis stripped — or null if the changelog has no (or an empty) section for it.</summary>
    public static string? Extract(string changelog, string version)
    {
        var lines = changelog.Replace("\r\n", "\n").Split('\n');
        int start = Array.FindIndex(lines, l => l.StartsWith($"## [{version}]", StringComparison.Ordinal));
        if (start < 0) return null;

        var sb = new StringBuilder();
        for (int i = start + 1; i < lines.Length; i++)
        {
            var l = lines[i];
            if (l.StartsWith("## ", StringComparison.Ordinal)) break;
            if (l.StartsWith("### ", StringComparison.Ordinal))
            {
                if (sb.Length > 0) sb.Append("\n\n");
                sb.Append(l[4..].Trim());
            }
            else if (l.StartsWith("- ", StringComparison.Ordinal)) sb.Append("\n• ").Append(l[2..].Trim());
            else if (l.StartsWith("  ", StringComparison.Ordinal) && l.Trim().Length > 0) sb.Append(' ').Append(l.Trim());
        }

        var text = sb.ToString().Replace("**", "").Replace("`", "").Trim();
        return text.Length == 0 ? null : text;
    }
}