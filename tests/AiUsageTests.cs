using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using TimeTracker.Core;
using Xunit;

namespace TimeTracker.Tests;

public sealed class AiUsageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tt-ai-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;

    public AiUsageTests() { Directory.CreateDirectory(_dir); _paths = new AppPaths(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // ---- endpoint classifier ----
    [Theory]
    [InlineData(null, EndpointClass.Anthropic)]
    [InlineData("https://api.anthropic.com", EndpointClass.Anthropic)]
    [InlineData("http://localhost:4000", EndpointClass.Local)]
    [InlineData("http://127.0.0.1:42515/v1", EndpointClass.Local)]
    [InlineData("https://myres.services.ai.azure.com/anthropic", EndpointClass.Foundry)]
    [InlineData("https://llm.corp.example.com", EndpointClass.Other)]
    [InlineData("not a url", EndpointClass.Other)]
    public void Classifies_base_urls(string? url, EndpointClass expected)
        => Assert.Equal(expected, EndpointClassifier.Classify(new AiEndpointInfo { BaseUrl = url }, null));

    [Fact]
    public void Foundry_flag_and_bedrock_flag()
    {
        Assert.Equal(EndpointClass.Foundry, EndpointClassifier.Classify(new AiEndpointInfo { UseFoundry = true }, null));
        Assert.Equal(EndpointClass.Other, EndpointClassifier.Classify(new AiEndpointInfo { UseBedrock = true }, null));
    }

    [Fact]
    public void Override_wins_over_localhost()
    {
        var o = new Dictionary<string, string> { ["127.0.0.1:42515"] = "Foundry" };
        Assert.Equal(EndpointClass.Foundry,
            EndpointClassifier.Classify(new AiEndpointInfo { BaseUrl = "http://127.0.0.1:42515" }, o));
    }

    // ---- pricing ----
    [Fact]
    public void Normalizes_and_prices_models()
    {
        var t = PricingTable.Defaults();
        Assert.NotNull(t.Find("anthropic.claude-sonnet-5-20260101-v1:0"));
        var cost = t.Cost(new List<ModelUsage> { new() { Model = "claude-sonnet-5", Input = 1_000_000, Output = 1_000_000 } }, out var unpriced);
        Assert.False(unpriced);
        Assert.Equal(12.0, cost, 6); // 2 + 10 per 1M
    }

    [Fact]
    public void Unknown_model_flags_unpriced()
    {
        var cost = PricingTable.Defaults().Cost(new List<ModelUsage> { new() { Model = "mystery-9", Input = 1000 } }, out var unpriced);
        Assert.True(unpriced);
        Assert.Equal(0, cost);
    }

    // ---- transcript parser ----
    private string Transcript(params string[] lines)
    {
        var p = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllText(p, string.Join("\n", lines));
        return p;
    }

    private static string Asst(string id, string model, int i, int o, int cr = 0, int c5 = 0, int c1 = 0)
        => $"{{\"type\":\"assistant\",\"message\":{{\"id\":\"{id}\",\"model\":\"{model}\",\"usage\":{{\"input_tokens\":{i},\"output_tokens\":{o},\"cache_read_input_tokens\":{cr},\"cache_creation_input_tokens\":{c5 + c1},\"cache_creation\":{{\"ephemeral_5m_input_tokens\":{c5},\"ephemeral_1h_input_tokens\":{c1}}}}}}}}}";

    [Fact]
    public void Parser_dedupes_by_id_keeping_last_and_groups_models()
    {
        var p = Transcript(
            Asst("a", "claude-sonnet-5", 10, 1),
            Asst("a", "claude-sonnet-5", 10, 50),
            Asst("b", "claude-haiku-4-5-20251001", 5, 5, cr: 100, c5: 7, c1: 3),
            "{\"type\":\"user\"}",
            "{\"message\":{\"id\":\"s\",\"model\":\"<synthetic>\",\"usage\":{\"input_tokens\":9}}}",
            "{\"message\":{\"id\":\"c\",\"model\":\"claude-sonnet-5\",\"usage\":{\"input_tok"); // truncated tail
        var r = TranscriptParser.Parse(p);
        Assert.Equal(2, r.Count);
        var s = r.Single(x => x.Model == "claude-sonnet-5");
        Assert.Equal(10, s.Input); Assert.Equal(50, s.Output);
        var h = r.Single(x => x.Model.StartsWith("claude-haiku"));
        Assert.Equal(100, h.CacheRead); Assert.Equal(7, h.CacheWrite5m); Assert.Equal(3, h.CacheWrite1h);
    }

    [Fact]
    public void Parser_missing_file_is_empty() => Assert.Empty(TranscriptParser.Parse(Path.Combine(_dir, "nope.jsonl")));

    // ---- session service ----
    private AiSessionService Svc(Settings? s = null, Project? live = null)
        => new(_paths, () => s ?? new Settings(), () => live, PricingTable.Defaults());

    private static AiPipeMessage Start(string id, string? url = null, string? tp = null)
        => new() { Kind = "start", SessionId = id, Cwd = "C:\\x", TranscriptPath = tp, Endpoint = new AiEndpointInfo { BaseUrl = url } };

    [Fact]
    public void Start_records_session_against_live_project_and_is_idempotent()
    {
        var svc = Svc(live: new Project { Id = "p1", Code = "PRJ" });
        svc.Handle(Start("s1"));
        svc.Handle(Start("s1"));
        var s = Assert.Single(svc.Snapshot());
        Assert.Equal("p1", s.ProjectId);
        Assert.Equal("PRJ", s.ProjectCode);
    }

    [Fact]
    public void Endpoint_filter_ignores_untracked_endpoints()
    {
        var svc = Svc();
        svc.Handle(Start("s1", "https://llm.corp.example.com"));
        Assert.Empty(svc.Snapshot());
    }

    [Fact]
    public void Disabled_tracking_ignores_everything()
    {
        var svc = Svc(new Settings { AiTrackingEnabled = false });
        svc.Handle(Start("s1"));
        Assert.Empty(svc.Snapshot());
    }

    [Fact]
    public void Cost_ping_handles_statusline_reset_without_losing_spend()
    {
        var svc = Svc();
        svc.Handle(Start("s1"));
        void Cost(double v) => svc.Handle(new AiPipeMessage { Kind = "cost", SessionId = "s1", TotalCostUsd = v });
        Cost(1.0); Cost(2.5);
        Assert.Equal(2.5, svc.Snapshot()[0].CostUsd, 6);
        Cost(0.4); // reset
        var s = svc.Snapshot()[0];
        Assert.Equal(2.9, s.CostUsd, 6);
        Assert.Equal("statusline", s.CostSource);
    }

    [Fact]
    public void Cost_ping_for_unknown_session_is_ignored()
    {
        var svc = Svc();
        svc.Handle(new AiPipeMessage { Kind = "cost", SessionId = "ghost", TotalCostUsd = 5 });
        Assert.Empty(svc.Snapshot());
    }

    [Fact]
    public void Transcript_refresh_prices_tokens_but_keeps_statusline_cost()
    {
        var tp = Transcript(Asst("a", "claude-sonnet-5", 1_000_000, 0));
        var svc = Svc();
        svc.Handle(Start("s1", tp: tp));
        Assert.True(svc.RefreshFromTranscript("claude", "s1"));
        var s = svc.Snapshot()[0];
        Assert.Equal(2.0, s.CostUsd, 6);
        Assert.Equal("transcript", s.CostSource);

        svc.Handle(new AiPipeMessage { Kind = "cost", SessionId = "s1", TotalCostUsd = 3.0 });
        svc.RefreshFromTranscript("claude", "s1");
        s = svc.Snapshot()[0];
        Assert.Equal(3.0, s.CostUsd, 6);
        Assert.Equal(1_000_000, s.InputTokens);
    }

    [Fact]
    public void Records_persist_across_instances()
    {
        Svc().Handle(Start("s1"));
        Assert.Single(Svc().Snapshot());
    }

    [Fact]
    public void Assign_changes_project()
    {
        var svc = Svc();
        svc.Handle(Start("s1"));
        Assert.True(svc.Assign("claude", "s1", new Project { Id = "p2", Code = "B" }));
        Assert.Equal("p2", svc.Snapshot()[0].ProjectId);
    }

    // ---- hook installer ----
    [Fact]
    public void Installer_merges_preserves_keys_chains_and_removes()
    {
        var sp = Path.Combine(_dir, "claude-settings.json");
        File.WriteAllText(sp, """
        { "theme": "dark",
          "statusLine": { "type": "command", "command": "my-status.cmd" },
          "hooks": { "SessionStart": [ { "hooks": [ { "type": "command", "command": "other.cmd" } ] } ] } }
        """);

        var prev = ClaudeHookInstaller.Install(_paths, sp);
        Assert.Equal("my-status.cmd", prev);
        Assert.True(File.Exists(sp + ".bak"));
        Assert.True(File.Exists(_paths.AiHookScript));

        var root = JsonNode.Parse(File.ReadAllText(sp))!.AsObject();
        Assert.Equal("dark", (string?)root["theme"]);
        Assert.Equal(2, root["hooks"]!["SessionStart"]!.AsArray().Count);
        Assert.Contains("-Chain", (string?)root["statusLine"]!["command"]);

        ClaudeHookInstaller.Install(_paths, sp); // idempotent
        Assert.Equal(2, JsonNode.Parse(File.ReadAllText(sp))!["hooks"]!["SessionStart"]!.AsArray().Count);

        ClaudeHookInstaller.Remove(sp, "my-status.cmd");
        root = JsonNode.Parse(File.ReadAllText(sp))!.AsObject();
        Assert.Single(root["hooks"]!["SessionStart"]!.AsArray());
        Assert.Equal("my-status.cmd", (string?)root["statusLine"]!["command"]);
    }

    [Fact]
    public void Installer_creates_settings_when_missing_and_remove_cleans_up()
    {
        var sp = Path.Combine(_dir, "sub", "settings.json");
        ClaudeHookInstaller.Install(_paths, sp);
        Assert.True(File.Exists(sp));
        ClaudeHookInstaller.Remove(sp);
        var root = JsonNode.Parse(File.ReadAllText(sp))!.AsObject();
        Assert.Null(root["hooks"]);
        Assert.Null(root["statusLine"]);
    }

    // ---- pipe ----
    [Fact]
    public async Task Pipe_round_trip_delivers_message()
    {
        var name = "tt-test-" + Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<AiPipeMessage>();
        using var server = new AiPipeServer(m => tcs.TrySetResult(m), name);
        server.Start();

        using (var c = new NamedPipeClientStream(".", name, PipeDirection.Out))
        {
            await c.ConnectAsync(3000);
            using var w = new StreamWriter(c);
            await w.WriteLineAsync(JsonSerializer.Serialize(new { kind = "start", sessionId = "abc", endpoint = new { baseUrl = "http://localhost:1" } }));
            await w.FlushAsync();
        }

        var got = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("abc", got.SessionId);
        Assert.Equal("http://localhost:1", got.Endpoint!.BaseUrl);
    }

    [Fact]
    public async Task Hook_script_delivers_start_message_end_to_end()
    {
        ClaudeHookInstaller.WriteScript(_paths);
        // Script targets the default pipe name; use a custom script copy pointing at a test pipe.
        var name = "tt-test-" + Guid.NewGuid().ToString("N");
        var script = Path.Combine(_dir, "hook-test.ps1");
        File.WriteAllText(script, ClaudeHookInstaller.ScriptText(name));

        var tcs = new TaskCompletionSource<AiPipeMessage>();
        using var server = new AiPipeServer(m => tcs.TrySetResult(m), name);
        server.Start();

        var psi = new System.Diagnostics.ProcessStartInfo("powershell",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\" -Mode start")
        { RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
        psi.Environment["ANTHROPIC_BASE_URL"] = "http://localhost:9999";
        psi.Environment["CLAUDE_CODE_USE_FOUNDRY"] = "1";
        using var proc = System.Diagnostics.Process.Start(psi)!;
        await proc.StandardInput.WriteAsync("{\"session_id\":\"sid-1\",\"cwd\":\"C:\\\\w\",\"transcript_path\":\"C:\\\\t.jsonl\",\"source\":\"startup\",\"model\":\"claude-sonnet-5\"}");
        proc.StandardInput.Close();

        var got = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("start", got.Kind);
        Assert.Equal("sid-1", got.SessionId);
        Assert.Equal("startup", got.Source);
        Assert.Equal("claude-sonnet-5", got.Model);
        Assert.True(got.Endpoint!.UseFoundry);
        Assert.Equal("http://localhost:9999", got.Endpoint.BaseUrl);
    }
}
