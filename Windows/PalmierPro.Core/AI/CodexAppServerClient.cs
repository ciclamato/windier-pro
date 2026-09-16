using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace PalmierPro.Core.AI;

/// <summary>
/// Small JSON-RPC client for the managed local Codex app-server process.
/// Authentication remains owned by Codex; Palmier only observes the documented protocol.
/// </summary>
public sealed class CodexAppServerClient : IAsyncDisposable
{
    private readonly string _executable;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private Process? _process;
    private Task? _reader;
    private long _nextId;

    public CodexAppServerClient(string executable = "codex") => _executable = executable;

    public event EventHandler<JsonElement>? NotificationReceived;

    public bool IsRunning => _process is { HasExited: false };

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning) return;
        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            },
            EnableRaisingEvents = true
        };
        _process.StartInfo.ArgumentList.Add("app-server");
        if (!_process.Start()) throw new InvalidOperationException("Could not start Codex app-server. Install Codex or choose another agent connection.");
        _reader = ReadLoopAsync(_process, cancellationToken);
        await Task.CompletedTask;
    }

    public Task<JsonElement> LoginAsync(string loginType = "chatgpt", CancellationToken cancellationToken = default) =>
        RequestAsync("account/login/start", new { type = loginType }, cancellationToken);

    public Task<JsonElement> ReadAccountAsync(CancellationToken cancellationToken = default) => RequestAsync("account/read", new { }, cancellationToken);

    public Task<JsonElement> LogoutAsync(CancellationToken cancellationToken = default) => RequestAsync("account/logout", new { }, cancellationToken);

    public Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken cancellationToken = default)
    {
        if (!IsRunning) throw new InvalidOperationException("Codex app-server is not running.");
        return RequestCoreAsync(method, parameters, cancellationToken);
    }

    private async Task<JsonElement> RequestCoreAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        var payload = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters });
        try
        {
            await _writeGate.WaitAsync(cancellationToken);
            try
            {
                await _process!.StandardInput.WriteLineAsync(payload);
                await _process.StandardInput.FlushAsync(cancellationToken);
            }
            finally { _writeGate.Release(); }
            return await completion.Task.WaitAsync(cancellationToken);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private async Task ReadLoopAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && !process.HasExited)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
                if (line is null) break;
                using var document = JsonDocument.Parse(line);
                var message = document.RootElement.Clone();
                if (message.TryGetProperty("id", out var idValue) && idValue.TryGetInt64(out var id) && _pending.TryGetValue(id, out var completion))
                {
                    if (message.TryGetProperty("error", out var error)) completion.TrySetException(new InvalidOperationException(error.ToString()));
                    else if (message.TryGetProperty("result", out var result)) completion.TrySetResult(result.Clone());
                    continue;
                }
                NotificationReceived?.Invoke(this, message);
            }
        }
        catch (Exception error)
        {
            foreach (var pending in _pending.Values) pending.TrySetException(error);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is null) return;
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch { }
        if (_reader is not null) await _reader.WaitAsync(TimeSpan.FromSeconds(2));
        _process.Dispose();
        _writeGate.Dispose();
    }
}
