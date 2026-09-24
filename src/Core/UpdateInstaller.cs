using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace TimeTracker.Core;

/// <summary>
/// Downloads a release's exe asset, verifies its SHA256 against the metadata, and stages it in a
/// temp file ready for the updater process to swap in. The actual file replacement is done by a
/// second instance of the app (see App.OnStartup's <c>--update</c> short-circuit) because the
/// running exe is locked while this process is alive.
/// </summary>
public sealed class UpdateInstaller
{
    private readonly HttpClient _http;

    public UpdateInstaller(HttpClient http) => _http = http;

    /// <summary>
    /// Download <paramref name="info"/>.AssetUrl to a temp file and verify its SHA256 matches
    /// <paramref name="info"/>.Sha256. Returns the temp file path, or null on any failure
    /// (network error, non-200, hash mismatch). The caller owns deleting the temp file.
    /// </summary>
    public async Task<string?> DownloadAsync(UpdateInfo info, CancellationToken ct = default)
    {
        string? tmp = null;
        try
        {
            tmp = Path.Combine(Path.GetTempPath(), $"timetracker-update-{Guid.NewGuid():N}.exe");
            using var req = new HttpRequestMessage(HttpMethod.Get, info.AssetUrl);
            req.Headers.UserAgent.Add(new ProductInfoHeaderValue("IFS-Time-Tracker", "1.0"));
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
            }

            var actual = await ComputeSha256Async(tmp, ct).ConfigureAwait(false);
            if (!string.Equals(actual, info.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(tmp);
                return null;
            }
            return tmp;
        }
        catch
        {
            if (tmp != null) TryDelete(tmp);
            return null;
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}