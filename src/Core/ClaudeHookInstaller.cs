using System.Text;
using System.Text.Json.Nodes;

namespace TimeTracker.Core;

/// <summary>Writes the PowerShell reporter and merges/removes its entries in Claude Code's settings.json.</summary>
public static class ClaudeHookInstaller
{
    public const string Marker = "ai-hook.ps1";

    public static string DefaultSettingsPath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    public static string ScriptText(string pipeName = AiPipeServer.DefaultPipeName) => $$"""
param([string]$Mode = 'start', [string]$Chain = '')
$ErrorActionPreference = 'SilentlyContinue'
$raw = [Console]::In.ReadToEnd()
try {
  $j = $raw | ConvertFrom-Json
  $m = [ordered]@{
    kind = $(if ($Mode -eq 'status') { 'cost' } else { 'start' })
    provider = 'claude'
    sessionId = $j.session_id
    cwd = $j.cwd
    transcriptPath = $j.transcript_path
    source = $j.source
    model = $(if ($j.model -is [string]) { $j.model } else { $j.model.id })
    endpoint = [ordered]@{
      baseUrl = $env:ANTHROPIC_BASE_URL
      useFoundry = ($env:CLAUDE_CODE_USE_FOUNDRY -in '1','true')
      hasFoundryVars = [bool](Get-ChildItem Env: | Where-Object { $_.Name -like 'ANTHROPIC_FOUNDRY_*' })
      useBedrock = ($env:CLAUDE_CODE_USE_BEDROCK -in '1','true')
      useVertex = ($env:CLAUDE_CODE_USE_VERTEX -in '1','true')
    }
  }
  if ($Mode -eq 'status') { $m.totalCostUsd = $j.cost.total_cost_usd }
  $p = New-Object System.IO.Pipes.NamedPipeClientStream('.', '{{pipeName}}', [System.IO.Pipes.PipeDirection]::Out)
  $p.Connect(200)
  $w = New-Object System.IO.StreamWriter($p)
  $w.WriteLine(($m | ConvertTo-Json -Compress -Depth 4))
  $w.Flush(); $p.Dispose()
} catch {}
if ($Mode -eq 'status' -and $Chain) { $raw | & cmd.exe /c $Chain }
""";

    public static void WriteScript(AppPaths paths) => File.WriteAllText(paths.AiHookScript, ScriptText(), new UTF8Encoding(false));

    private static string Command(AppPaths paths, string mode, string? chain)
    {
        var c = $"powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{paths.AiHookScript}\" -Mode {mode}";
        return string.IsNullOrWhiteSpace(chain) ? c : c + $" -Chain \"{chain.Replace("\"", "\\\"")}\"";
    }

    /// <summary>Adds the async SessionStart hook and the cost statusline. Returns the previous statusline
    /// command (if one wasn't ours) so the caller can store it as the chained command.</summary>
    public static string? Install(AppPaths paths, string? settingsPath = null, string? chain = null)
    {
        settingsPath ??= DefaultSettingsPath();
        WriteScript(paths);
        var root = ReadRoot(settingsPath);
        Backup(settingsPath);

        string? previous = null;
        if (root["statusLine"] is JsonObject sl && sl["command"]?.GetValue<string>() is { } cmd && !cmd.Contains(Marker))
            previous = cmd;
        chain ??= previous;

        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        root["hooks"] = hooks;
        var start = hooks["SessionStart"] as JsonArray ?? new JsonArray();
        hooks["SessionStart"] = start;
        RemoveOurs(start);
        start.Add(new JsonObject
        {
            ["hooks"] = new JsonArray(new JsonObject
            {
                ["type"] = "command",
                ["command"] = Command(paths, "start", null),
                ["async"] = true,
            }),
        });

        root["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = Command(paths, "status", chain) };
        Write(settingsPath, root);
        return previous;
    }

    /// <summary>Removes our hook and statusline; restores <paramref name="restoreStatusLine"/> if given.</summary>
    public static void Remove(string? settingsPath = null, string? restoreStatusLine = null)
    {
        settingsPath ??= DefaultSettingsPath();
        if (!File.Exists(settingsPath)) return;
        var root = ReadRoot(settingsPath);
        Backup(settingsPath);

        if (root["hooks"] is JsonObject hooks && hooks["SessionStart"] is JsonArray start)
        {
            RemoveOurs(start);
            if (start.Count == 0) hooks.Remove("SessionStart");
            if (hooks.Count == 0) root.Remove("hooks");
        }
        if (root["statusLine"] is JsonObject sl && sl["command"]?.GetValue<string>() is { } cmd && cmd.Contains(Marker))
        {
            if (string.IsNullOrWhiteSpace(restoreStatusLine)) root.Remove("statusLine");
            else root["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = restoreStatusLine };
        }
        Write(settingsPath, root);
    }

    private static void RemoveOurs(JsonArray start)
    {
        for (var i = start.Count - 1; i >= 0; i--)
        {
            var inner = (start[i] as JsonObject)?["hooks"] as JsonArray;
            if (inner is null) continue;
            for (var k = inner.Count - 1; k >= 0; k--)
                if (inner[k]?["command"]?.GetValue<string>()?.Contains(Marker) == true) inner.RemoveAt(k);
            if (inner.Count == 0) start.RemoveAt(i);
        }
    }

    private static JsonObject ReadRoot(string path)
        => File.Exists(path) && File.ReadAllText(path).Trim().Length > 0
            ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject()
            : new JsonObject();

    private static void Backup(string path)
    {
        if (File.Exists(path)) File.Copy(path, path + ".bak", true);
    }

    private static void Write(string path, JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, root.ToJsonString(new() { WriteIndented = true }));
    }
}
