using System.Text.Json;
using System.Text.RegularExpressions;

namespace TimeTracker.Core;

/// <summary>USD per 1M tokens. Null cache fields fall back to the usual multiples of input price.</summary>
public sealed class ModelPrice
{
    public double Input { get; set; }
    public double Output { get; set; }
    public double? CacheRead { get; set; }
    public double? CacheWrite5m { get; set; }
    public double? CacheWrite1h { get; set; }

    public double CacheReadRate => CacheRead ?? Input * 0.10;
    public double CacheWrite5mRate => CacheWrite5m ?? Input * 1.25;
    public double CacheWrite1hRate => CacheWrite1h ?? Input * 2.0;
}

public sealed class ModelUsage
{
    public string Model { get; set; } = "";
    public long Input { get; set; }
    public long Output { get; set; }
    public long CacheRead { get; set; }
    public long CacheWrite5m { get; set; }
    public long CacheWrite1h { get; set; }
}

/// <summary>Editable per-model price list (data\pricing.json). List-price estimates, not billing.</summary>
public sealed class PricingTable
{
    private readonly Dictionary<string, ModelPrice> _prices;

    public PricingTable(Dictionary<string, ModelPrice> prices) => _prices = prices;

    public static PricingTable Defaults() => new(new Dictionary<string, ModelPrice>
    {
        ["claude-fable-5-1"] = new() { Input = 10, Output = 50, CacheRead = 0.25 },
        ["claude-fable-5"]   = new() { Input = 10, Output = 50 },
        ["claude-opus-5-5"]  = new() { Input = 4, Output = 20, CacheRead = 0.20 },
        ["claude-opus-5"]    = new() { Input = 5, Output = 25 },
        ["claude-opus-4-8"]  = new() { Input = 5, Output = 25 },
        ["claude-opus-4-7"]  = new() { Input = 5, Output = 25 },
        ["claude-opus-4-6"]  = new() { Input = 5, Output = 25 },
        ["claude-sonnet-5"]  = new() { Input = 2, Output = 10 },
        ["claude-sonnet-4-6"] = new() { Input = 3, Output = 15 },
        ["claude-haiku-4-5"] = new() { Input = 1, Output = 5 },
    });

    /// <summary>Loads the user's file if present and valid, else the defaults. A user file replaces
    /// the defaults entirely (so it can also remove or reprice models).</summary>
    public static PricingTable Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, ModelPrice>>(
                    File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (loaded is { Count: > 0 })
                    return new PricingTable(loaded.ToDictionary(kv => Normalize(kv.Key), kv => kv.Value));
            }
        }
        catch { /* fall through to defaults */ }
        return Defaults();
    }

    public static void WriteDefaults(string path)
    {
        if (File.Exists(path)) return;
        File.WriteAllText(path, JsonSerializer.Serialize(Defaults()._prices,
            new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Lower-case, drop provider prefix / [1m] / @version / date suffix, dots to dashes.</summary>
    public static string Normalize(string model)
    {
        var m = model.Trim().ToLowerInvariant();
        if (m.StartsWith("anthropic.")) m = m["anthropic.".Length..];
        m = Regex.Replace(m, @"\[.*?\]", "");
        var at = m.IndexOf('@'); if (at >= 0) m = m[..at];
        m = m.Replace('.', '-');
        m = Regex.Replace(m, @"-\d{8}$", "");
        m = Regex.Replace(m, @"-v\d+:\d+$", "");
        return m;
    }

    /// <summary>Exact match, else the longest key that prefixes the model id.</summary>
    public ModelPrice? Find(string model)
    {
        var n = Normalize(model);
        if (_prices.TryGetValue(n, out var exact)) return exact;
        return _prices.Where(kv => n.StartsWith(kv.Key + "-") || n.StartsWith(kv.Key))
                      .OrderByDescending(kv => kv.Key.Length)
                      .Select(kv => kv.Value).FirstOrDefault();
    }

    /// <summary>Total USD for the usage; unpriced models contribute 0 and set <paramref name="unpriced"/>.</summary>
    public double Cost(IEnumerable<ModelUsage> usage, out bool unpriced)
    {
        unpriced = false;
        double total = 0;
        foreach (var u in usage)
        {
            var p = Find(u.Model);
            if (p is null) { unpriced = true; continue; }
            total += (u.Input * p.Input + u.Output * p.Output + u.CacheRead * p.CacheReadRate
                      + u.CacheWrite5m * p.CacheWrite5mRate + u.CacheWrite1h * p.CacheWrite1hRate) / 1_000_000.0;
        }
        return total;
    }
}
