using System.Net;
using System.Text;
using System.Text.Json;
using PalmierPro.Core.Editing;
using PalmierPro.Core.Models;

namespace PalmierPro.Core.Automation;

public sealed class McpLoopbackServer : IAsyncDisposable
{
    public const int DefaultPort = 19789;
    private readonly EditorDocument _document;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly int _port;
    private Task? _loop;

    public McpLoopbackServer(EditorDocument document, int port = DefaultPort)
    {
        if (port is < 1 or > 65_535) throw new ArgumentOutOfRangeException(nameof(port));
        _document = document;
        _port = port;
        Endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
    }

    public Uri Endpoint { get; }
    public bool IsRunning => _listener.IsListening;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning) return Task.CompletedTask;
        try
        {
            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/mcp/");
            _listener.Start();
        }
        catch (HttpListenerException error)
        {
            throw new InvalidOperationException($"Palmier MCP could not bind {Endpoint}. Another process may be using the port; choose a different port in settings.", error);
        }
        _loop = ListenAsync(cancellationToken);
        return Task.CompletedTask;
    }

    private async Task ListenAsync(CancellationToken externalCancellation)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, externalCancellation);
        while (!cancellation.Token.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync().WaitAsync(cancellation.Token); }
            catch (OperationCanceledException) { break; }
            catch (HttpListenerException) when (!_listener.IsListening) { break; }
            _ = Task.Run(() => HandleAsync(context, cancellation.Token), cancellation.Token);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        using var response = context.Response;
        if (context.Request.HttpMethod != "POST" || context.Request.Url?.AbsolutePath is not ("/mcp" or "/mcp/"))
        {
            response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
            return;
        }
        if (!string.IsNullOrWhiteSpace(context.Request.Headers["Origin"]))
        {
            response.StatusCode = (int)HttpStatusCode.Forbidden;
            await WriteJsonAsync(response, new { error = "Browser-origin MCP requests are not accepted." }, cancellationToken);
            return;
        }
        try
        {
            using var document = await JsonDocument.ParseAsync(context.Request.InputStream, cancellationToken: cancellationToken);
            var result = await DispatchAsync(document.RootElement, cancellationToken);
            if (result is null) { response.StatusCode = (int)HttpStatusCode.Accepted; return; }
            response.StatusCode = (int)HttpStatusCode.OK;
            await WriteJsonAsync(response, result, cancellationToken);
        }
        catch (Exception error)
        {
            response.StatusCode = (int)HttpStatusCode.OK;
            await WriteJsonAsync(response, new
            {
                jsonrpc = "2.0",
                id = (string?)null,
                error = new { code = -32603, message = error.Message }
            }, cancellationToken);
        }
    }

    private async Task<object?> DispatchAsync(JsonElement request, CancellationToken cancellationToken)
    {
        var method = request.TryGetProperty("method", out var methodValue) ? methodValue.GetString() : null;
        var id = request.TryGetProperty("id", out var idValue) ? idValue.Clone() : (JsonElement?)null;
        if (method is null) return Error(id, -32600, "A JSON-RPC method is required.");
        return method switch
        {
            "initialize" => new
            {
                jsonrpc = "2.0", id,
                result = new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { tools = new { listChanged = false } },
                    serverInfo = new { name = "palmier-windows", version = "0.1.0" }
                }
            },
            "notifications/initialized" => null,
            "ping" => new { jsonrpc = "2.0", id, result = new { } },
            "tools/list" => new { jsonrpc = "2.0", id, result = new { tools = ToolCatalog() } },
            "tools/call" => await CallToolAsync(id, request, cancellationToken),
            _ => Error(id, -32601, $"Unknown MCP method '{method}'.")
        };
    }

    private async Task<object> CallToolAsync(JsonElement? id, JsonElement request, CancellationToken cancellationToken)
    {
        var parameters = request.TryGetProperty("params", out var value) ? value : default;
        var name = parameters.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
        var arguments = parameters.TryGetProperty("arguments", out var argumentValue) ? argumentValue : default;
        try
        {
            object content;
            switch (name)
            {
                case "inspect_timeline":
                    content = await InspectTimelineAsync(cancellationToken);
                    break;
                case "undo":
                    content = await _document.UndoAsync(cancellationToken);
                    break;
                case "redo":
                    content = await _document.RedoAsync(cancellationToken);
                    break;
                case "move_clip":
                    content = await ExecuteAsync(new MoveClipCommand(
                        Required(arguments, "timelineId"), Required(arguments, "clipId"),
                        arguments.GetProperty("startFrame").GetInt32(), Optional(arguments, "trackId")), cancellationToken);
                    break;
                case "trim_clip":
                    content = await ExecuteAsync(new TrimClipCommand(
                        Required(arguments, "timelineId"), Required(arguments, "clipId"),
                        arguments.GetProperty("durationFrames").GetInt32(),
                        arguments.TryGetProperty("trimStartFrame", out var trimStart) ? trimStart.GetInt32() : null), cancellationToken);
                    break;
                case "split_clip":
                    content = await ExecuteAsync(new SplitClipCommand(
                        Required(arguments, "timelineId"), Required(arguments, "clipId"), arguments.GetProperty("atFrame").GetInt32()), cancellationToken);
                    break;
                default:
                    return new { jsonrpc = "2.0", id, error = new { code = -32602, message = $"Unknown Palmier tool '{name}'." } };
            }
            return new { jsonrpc = "2.0", id, result = new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(content) } }, isError = false } };
        }
        catch (Exception error)
        {
            return new { jsonrpc = "2.0", id, result = new { content = new[] { new { type = "text", text = error.Message } }, isError = true } };
        }
    }

    private async Task<CommandReceipt> ExecuteAsync(IEditorCommand command, CancellationToken cancellationToken) => await _document.ExecuteAsync(command, cancellationToken);

    private async Task<object> InspectTimelineAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _document.SnapshotAsync(cancellationToken);
        var timeline = snapshot.Project.Timelines.FirstOrDefault(t => t.Id == snapshot.Project.ActiveTimelineId) ?? snapshot.Project.Timelines.FirstOrDefault();
        return timeline is null ? new { timelines = Array.Empty<object>() } : new
        {
            timeline = new
            {
                id = timeline.Id, timeline.Name, timeline.Fps, timeline.Width, timeline.Height,
                totalFrames = timeline.TotalFrames,
                tracks = timeline.Tracks.Select(track => new
                {
                    id = track.Id, track.Type, track.Name,
                    clips = track.Clips.Select(clip => new { clip.Id, clip.MediaRef, clip.MediaType, clip.StartFrame, clip.DurationFrames, clip.EndFrame })
                })
            }
        };
    }

    private static object[] ToolCatalog() =>
    [
        new { name = "inspect_timeline", description = "Read the active timeline and clips.", inputSchema = new { type = "object", properties = new { } } },
        new { name = "undo", description = "Undo the last editor command.", inputSchema = new { type = "object", properties = new { } } },
        new { name = "redo", description = "Redo the last undone editor command.", inputSchema = new { type = "object", properties = new { } } },
        new { name = "move_clip", description = "Move a clip through the shared undoable command dispatcher.", inputSchema = new { type = "object", required = new[] { "timelineId", "clipId", "startFrame" }, properties = new { timelineId = new { type = "string" }, clipId = new { type = "string" }, startFrame = new { type = "integer" }, trackId = new { type = "string" } } } },
        new { name = "trim_clip", description = "Trim a clip through the shared undoable command dispatcher.", inputSchema = new { type = "object", required = new[] { "timelineId", "clipId", "durationFrames" }, properties = new { timelineId = new { type = "string" }, clipId = new { type = "string" }, durationFrames = new { type = "integer" }, trimStartFrame = new { type = "integer" } } } },
        new { name = "split_clip", description = "Split a clip at a timeline frame.", inputSchema = new { type = "object", required = new[] { "timelineId", "clipId", "atFrame" }, properties = new { timelineId = new { type = "string" }, clipId = new { type = "string" }, atFrame = new { type = "integer" } } } }
    ];

    private static object Error(JsonElement? id, int code, string message) => new { jsonrpc = "2.0", id, error = new { code, message } };

    private static string Required(JsonElement arguments, string property) => arguments.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString()! : throw new ArgumentException($"'{property}' is required.");

    private static string? Optional(JsonElement arguments, string property) => arguments.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static async Task WriteJsonAsync(HttpListenerResponse response, object value, CancellationToken cancellationToken)
    {
        var data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value));
        response.ContentType = "application/json";
        response.ContentEncoding = Encoding.UTF8;
        response.ContentLength64 = data.Length;
        await response.OutputStream.WriteAsync(data, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_listener.IsListening) _listener.Stop();
        if (_loop is not null) await _loop.WaitAsync(TimeSpan.FromSeconds(2));
        _listener.Close();
        _stop.Dispose();
    }
}
