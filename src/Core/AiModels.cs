namespace TimeTracker.Core;

public enum EndpointClass { Anthropic, Foundry, Local, Other }

/// <summary>Endpoint-related environment of the Claude Code process, captured by the hook.</summary>
public sealed class AiEndpointInfo
{
    public string? BaseUrl { get; set; }
    public bool UseFoundry { get; set; }
    public bool HasFoundryVars { get; set; }
    public bool UseBedrock { get; set; }
    public bool UseVertex { get; set; }
}

public static class EndpointClassifier
{
    private static readonly string[] AzureSuffixes =
        { ".services.ai.azure.com", ".openai.azure.com", ".cognitiveservices.azure.com" };

    /// <param name="overrides">Optional "host" or "host:port" -> class name map, checked first, so a
    /// known local proxy can be declared as fronting Foundry/Anthropic.</param>
    public static EndpointClass Classify(AiEndpointInfo info, IReadOnlyDictionary<string, string>? overrides = null)
    {
        if (info.UseBedrock || info.UseVertex) return EndpointClass.Other;
        if (info.UseFoundry || info.HasFoundryVars) return EndpointClass.Foundry;

        if (string.IsNullOrWhiteSpace(info.BaseUrl)) return EndpointClass.Anthropic;
        if (!Uri.TryCreate(info.BaseUrl.Trim(), UriKind.Absolute, out var uri)) return EndpointClass.Other;

        var host = uri.Host.ToLowerInvariant();
        if (overrides is not null)
        {
            foreach (var key in new[] { $"{host}:{uri.Port}", host })
            {
                foreach (var kv in overrides)
                {
                    if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)
                        && Enum.TryParse<EndpointClass>(kv.Value, true, out var cls))
                        return cls;
                }
            }
        }

        if (host is "localhost" or "127.0.0.1" or "::1" or "[::1]") return EndpointClass.Local;
        if (host == "anthropic.com" || host.EndsWith(".anthropic.com")) return EndpointClass.Anthropic;
        if (AzureSuffixes.Any(host.EndsWith)) return EndpointClass.Foundry;
        return EndpointClass.Other;
    }
}

/// <summary>One AI coding session, assigned (or not) to a project. Costs are derived data.</summary>
public sealed class AiSession
{
    public AiSession ShallowCopy() => (AiSession)MemberwiseClone();
    public string Provider { get; set; } = "claude";
    public string SessionId { get; set; } = "";
    public string? ProjectId { get; set; }
    public string? ProjectCode { get; set; }
    public string Cwd { get; set; } = "";
    public EndpointClass Endpoint { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public string? Model { get; set; }
    public string? TranscriptPath { get; set; }

    public double CostUsd { get; set; }
    /// <summary>"statusline" or "transcript"; empty until a cost has been recorded.</summary>
    public string CostSource { get; set; } = "";
    /// <summary>Spend carried over from before the statusline total last reset to a lower value.</summary>
    public double CostBaseUsd { get; set; }
    public double LastReportedUsd { get; set; }

    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheWriteTokens { get; set; }
    /// <summary>Token usage whose model had no price entry (cost is understated).</summary>
    public bool HasUnpricedUsage { get; set; }
}

/// <summary>Message sent by the hook / statusline scripts over the named pipe (one JSON line).</summary>
public sealed class AiPipeMessage
{
    /// <summary>"start" (SessionStart hook) or "cost" (statusline).</summary>
    public string Kind { get; set; } = "";
    public string Provider { get; set; } = "claude";
    public string SessionId { get; set; } = "";
    public string? Cwd { get; set; }
    public string? TranscriptPath { get; set; }
    public string? Source { get; set; }
    public string? Model { get; set; }
    public AiEndpointInfo? Endpoint { get; set; }
    public double? TotalCostUsd { get; set; }
}
