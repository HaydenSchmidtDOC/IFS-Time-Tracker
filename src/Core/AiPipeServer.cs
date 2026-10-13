using System.IO.Pipes;
using System.Text.Json;

namespace TimeTracker.Core;

/// <summary>Per-user named pipe the hook and statusline scripts write one JSON line to.</summary>
public sealed class AiPipeServer : IDisposable
{
    public const string DefaultPipeName = "TimeTracker.AiSessions";
    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

    private readonly string _name;
    private readonly Action<AiPipeMessage> _onMessage;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public AiPipeServer(Action<AiPipeMessage> onMessage, string name = DefaultPipeName)
    {
        _onMessage = onMessage;
        _name = name;
    }

    public void Start() => _loop = Task.Run(Loop);

    private async Task Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                // CurrentUserOnly keeps other local users from talking to the pipe.
                await using var server = new NamedPipeServerStream(_name, PipeDirection.In, 4,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                using var reader = new StreamReader(server);
                var line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line)) continue;
                var msg = JsonSerializer.Deserialize<AiPipeMessage>(line, Opts);
                if (msg is not null) _onMessage(msg);
            }
            catch (OperationCanceledException) { break; }
            catch { await Task.Delay(200).ConfigureAwait(false); }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(500); } catch { }
        _cts.Dispose();
    }
}
