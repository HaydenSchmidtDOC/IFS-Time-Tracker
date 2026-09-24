namespace TimeTracker.Core;

/// <summary>
/// Metadata about an available update, parsed from the release's <c>latest.json</c> asset.
/// Carries everything the updater needs to download and verify the new exe without parsing the
/// GitHub release API's HTML or guessing at asset names.
/// </summary>
public sealed record UpdateInfo(Version Version, string AssetUrl, string Sha256, string ReleaseUrl);

/// <summary>
/// Parsing and comparison helpers for update metadata — kept static and free of any I/O so the
/// version logic is trivially unit-testable without a network or a fake HTTP handler.
/// </summary>
public static class UpdateMetadata
{
    /// <summary>
    /// Parse the <c>latest.json</c> release asset. Expected shape:
    /// <code>
    /// { "version": "0.6.0", "assetUrl": "...", "sha256": "...", "releaseUrl": "..." }
    /// </code>
    /// Returns null if any required field is missing or the version doesn't parse.
    /// </summary>
    public static UpdateInfo? Parse(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("version", out var v) || v.ValueKind != System.Text.Json.JsonValueKind.String)
                return null;
            if (!TryParseVersion(v.GetString()!, out var version))
                return null;
            if (!root.TryGetProperty("assetUrl", out var a) || a.ValueKind != System.Text.Json.JsonValueKind.String)
                return null;
            if (!root.TryGetProperty("sha256", out var s) || s.ValueKind != System.Text.Json.JsonValueKind.String)
                return null;
            var releaseUrl = root.TryGetProperty("releaseUrl", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.String
                ? r.GetString()!
                : "";
            return new UpdateInfo(version, a.GetString()!, s.GetString()!, releaseUrl);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Parse a release tag like "v0.6.0" (leading 'v' optional) into a Version.</summary>
    public static bool TryParseVersion(string tag, out Version version)
    {
        var t = tag.Trim();
        if (t.StartsWith('v') || t.StartsWith('V')) t = t[1..];
        return Version.TryParse(t, out version!);
    }

    /// <summary>True when <paramref name="latest"/> is strictly newer than <paramref name="current"/>.</summary>
    public static bool IsNewer(Version current, Version latest) => latest > current;
}