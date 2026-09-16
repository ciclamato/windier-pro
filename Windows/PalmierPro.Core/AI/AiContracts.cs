using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace PalmierPro.Core.AI;

public enum ProviderAuthMode
{
    ApiKey,
    BrowserOAuth,
    DeviceCode,
    LocalAgent
}

public enum GenerationModality
{
    Image,
    Video,
    Speech,
    Music,
    Upscale
}

public sealed record ProviderCapabilities(
    string Id,
    string DisplayName,
    IReadOnlyList<ProviderAuthMode> AuthModes,
    IReadOnlyList<GenerationModality> Modalities,
    bool SupportsStreaming,
    Uri? DocumentationUri = null);

public sealed record AgentMessage(string Role, string Content);

public sealed record AgentRequestOptions(string? Model = null, double? Temperature = null, int? MaxOutputTokens = null);

public sealed record AgentReply(string ProviderId, string? Model, string Text, string? RequestId = null);

public interface IAgentProvider
{
    ProviderCapabilities Capabilities { get; }
    Task<AgentReply> CompleteAsync(IReadOnlyList<AgentMessage> messages, AgentRequestOptions options, CancellationToken cancellationToken = default);
}

public interface IMediaGenerationProvider
{
    ProviderCapabilities Capabilities { get; }
    Task<GenerationJob> SubmitAsync(GenerationRequest request, CancellationToken cancellationToken = default);
    Task<GenerationJob> GetAsync(string jobId, CancellationToken cancellationToken = default);
}

public sealed record GenerationRequest(GenerationModality Modality, string Prompt, string? Model = null, TimeSpan? Duration = null);

public sealed record GenerationJob(string ProviderId, string Id, string State, IReadOnlyList<Uri> Outputs, string? Error = null);

public sealed class OpenAiCompatibleProvider : IAgentProvider
{
    private readonly HttpClient _http;
    private readonly Func<CancellationToken, Task<string?>> _apiKey;

    public OpenAiCompatibleProvider(string id, string displayName, Uri endpoint, Func<CancellationToken, Task<string?>> apiKey, HttpClient? httpClient = null)
    {
        Capabilities = new ProviderCapabilities(id, displayName, [ProviderAuthMode.ApiKey, ProviderAuthMode.LocalAgent], [], true);
        Endpoint = endpoint;
        _apiKey = apiKey;
        _http = httpClient ?? new HttpClient();
    }

    public ProviderCapabilities Capabilities { get; }
    public Uri Endpoint { get; }

    public async Task<AgentReply> CompleteAsync(IReadOnlyList<AgentMessage> messages, AgentRequestOptions options, CancellationToken cancellationToken = default)
    {
        var key = await _apiKey(cancellationToken) ?? throw new InvalidOperationException($"{Capabilities.DisplayName} is not connected.");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Endpoint, "chat/completions"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = JsonContent.Create(new
        {
            model = options.Model ?? "gpt-4o-mini",
            messages = messages.Select(message => new { role = message.Role, content = message.Content }),
            temperature = options.Temperature,
            max_tokens = options.MaxOutputTokens,
            stream = false
        });
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"{Capabilities.DisplayName} returned {(int)response.StatusCode}: {body}");
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var text = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
        return new AgentReply(Capabilities.Id, root.TryGetProperty("model", out var model) ? model.GetString() : options.Model, text, root.TryGetProperty("id", out var id) ? id.GetString() : null);
    }
}

public sealed class AnthropicProvider : IAgentProvider
{
    private readonly HttpClient _http;
    private readonly Func<CancellationToken, Task<string?>> _apiKey;

    public AnthropicProvider(Func<CancellationToken, Task<string?>> apiKey, HttpClient? httpClient = null)
    {
        Capabilities = new ProviderCapabilities("anthropic", "Anthropic", [ProviderAuthMode.ApiKey], [], true);
        _apiKey = apiKey;
        _http = httpClient ?? new HttpClient();
    }

    public ProviderCapabilities Capabilities { get; }

    public async Task<AgentReply> CompleteAsync(IReadOnlyList<AgentMessage> messages, AgentRequestOptions options, CancellationToken cancellationToken = default)
    {
        var key = await _apiKey(cancellationToken) ?? throw new InvalidOperationException("Anthropic is not connected.");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        request.Headers.TryAddWithoutValidation("x-api-key", key);
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        request.Content = JsonContent.Create(new
        {
            model = options.Model ?? "claude-3-5-haiku-latest",
            max_tokens = options.MaxOutputTokens ?? 2048,
            messages = messages.Where(message => message.Role != "system").Select(message => new { role = message.Role, content = message.Content }),
            system = messages.FirstOrDefault(message => message.Role == "system")?.Content
        });
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Anthropic returned {(int)response.StatusCode}: {body}");
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var text = root.GetProperty("content")[0].GetProperty("text").GetString() ?? string.Empty;
        return new AgentReply(Capabilities.Id, root.TryGetProperty("model", out var model) ? model.GetString() : options.Model, text, root.TryGetProperty("id", out var id) ? id.GetString() : null);
    }
}

public sealed class ProviderRegistry
{
    private readonly Dictionary<string, IAgentProvider> _providers = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<IAgentProvider> Providers => _providers.Values;

    public void Register(IAgentProvider provider) => _providers[provider.Capabilities.Id] = provider;

    public IAgentProvider Get(string id) => _providers.TryGetValue(id, out var provider)
        ? provider
        : throw new KeyNotFoundException($"AI provider '{id}' is not registered.");
}
