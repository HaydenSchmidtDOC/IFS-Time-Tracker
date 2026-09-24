using System.Net.Http;
using System.Net.Http.Headers;

namespace TimeTracker.Core;

/// <summary>
/// Checks GitHub Releases for a newer build. The release carries a <c>latest.json</c> asset
/// (version + asset URL + SHA256) that the release workflow uploads alongside the exe, so the
/// checker only needs to fetch that one small file — no HTML parsing, no guessing asset names.
/// </summary>
public sealed class UpdateChecker
{
    private readonly HttpClient _http;
    private readonly string _owner;
    private readonly string _repo;

    public UpdateChecker(HttpClient http, string owner, string repo)
    {
        _http = http;
        _owner = owner;
        _repo = repo;
    }

    /// <summary>
    /// The URL of the <c>latest.json</c> asset for the latest release. GitHub serves release
    /// assets from a redirecting URL; the checker follows it to the raw asset.
    /// </summary>
    public string LatestMetadataUrl
        => $"https://github.com/{_owner}/{_repo}/releases/latest/download/latest.json";

    /// <summary>
    /// Fetch and parse the latest release's metadata. Returns null when there's no release yet,
    /// the metadata is missing/unparseable, or the network call fails — all treated as "no
    /// update" so a transient failure never nags the user.
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, LatestMetadataUrl);
            req.Headers.UserAgent.Add(new ProductInfoHeaderValue("IFS-Time-Tracker", "1.0"));
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return UpdateMetadata.Parse(json);
        }
        catch
        {
            return null;
        }
    }
}