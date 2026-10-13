using System.Text.Json;

namespace TimeTracker.Core;

/// <summary>Sums Claude Code transcript usage per model.</summary>
public static class TranscriptParser
{
    /// <summary>
    /// Reads a session .jsonl. Streaming writes the same assistant message several times, so lines are
    /// de-duplicated by message id keeping the last occurrence. Malformed/partial lines (the file may be
    /// mid-write) are skipped. Synthetic messages are ignored.
    /// </summary>
    public static List<ModelUsage> Parse(string path)
    {
        var byId = new Dictionary<string, ModelUsage>();
        var anon = new List<ModelUsage>();
        if (!File.Exists(path)) return new();

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        string? line;
        while ((line = sr.ReadLine()) is not null)
        {
            if (line.Length == 0 || !line.Contains("\"usage\"")) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("message", out var msg)
                    || msg.ValueKind != JsonValueKind.Object
                    || !msg.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object)
                    continue;

                var model = msg.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
                if (model.Length == 0 || model.StartsWith("<")) continue;

                var cw5 = 0L; var cw1 = 0L;
                var cacheCreate = Get(u, "cache_creation_input_tokens");
                if (u.TryGetProperty("cache_creation", out var cc) && cc.ValueKind == JsonValueKind.Object)
                {
                    cw5 = Get(cc, "ephemeral_5m_input_tokens");
                    cw1 = Get(cc, "ephemeral_1h_input_tokens");
                }
                else cw5 = cacheCreate;

                var mu = new ModelUsage
                {
                    Model = model,
                    Input = Get(u, "input_tokens"),
                    Output = Get(u, "output_tokens"),
                    CacheRead = Get(u, "cache_read_input_tokens"),
                    CacheWrite5m = cw5,
                    CacheWrite1h = cw1,
                };

                var id = msg.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                if (string.IsNullOrEmpty(id)) anon.Add(mu); else byId[id] = mu;
            }
            catch (JsonException) { /* partial line */ }
        }

        return byId.Values.Concat(anon)
            .GroupBy(x => x.Model)
            .Select(g => new ModelUsage
            {
                Model = g.Key,
                Input = g.Sum(x => x.Input), Output = g.Sum(x => x.Output),
                CacheRead = g.Sum(x => x.CacheRead),
                CacheWrite5m = g.Sum(x => x.CacheWrite5m), CacheWrite1h = g.Sum(x => x.CacheWrite1h),
            }).ToList();
    }

    private static long Get(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
}
