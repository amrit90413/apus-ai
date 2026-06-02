using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Gateway.Api.Providers;

public sealed class AnthropicOptions
{
    public string ApiKey { get; set; } = "";       // injected from K8s secret — never leaves the server
    public string BaseUrl { get; set; } = "https://api.anthropic.com";
    public string Version { get; set; } = "2023-06-01";
}

/// <summary>
/// Anthropic Claude provider. Parses the Messages API SSE stream, surfaces text
/// deltas as they arrive, and extracts the authoritative token usage from the
/// message_start (input) and message_delta (output) events.
/// </summary>
public sealed class AnthropicProvider : IAiProvider
{
    private readonly HttpClient _http;
    private readonly AnthropicOptions _opt;
    private readonly IProviderKeyService _keyService;

    public AnthropicProvider(HttpClient http, IOptions<AnthropicOptions> opt, IProviderKeyService keyService)
    {
        _http = http;
        _opt = opt.Value;
        _keyService = keyService;
    }

    public string Name => "anthropic";
    public bool Supports(string model) => model.StartsWith("claude", StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<ChatChunk> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // DB key takes priority over env var so admins can rotate without redeploying.
        var apiKey = await _keyService.GetActiveKeyAsync("anthropic", ct) ?? _opt.ApiKey;

        var system = request.Messages.FirstOrDefault(m => m.Role == "system")?.Content;
        var body = new
        {
            model = request.Model,
            max_tokens = request.MaxTokens,
            stream = true,
            system,
            messages = request.Messages
                .Where(m => m.Role != "system")
                .Select(m => new { role = m.Role, content = m.Content })
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_opt.BaseUrl}/v1/messages");
        req.Headers.Add("x-api-key", apiKey);
        req.Headers.Add("anthropic-version", _opt.Version);
        req.Content = JsonContent.Create(body);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        int inputTokens = 0, outputTokens = 0;
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (!line.StartsWith("data:")) continue;
            var json = line["data:".Length..].Trim();
            if (json is "" or "[DONE]") continue;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();

            switch (type)
            {
                case "message_start":
                    inputTokens = root.GetProperty("message").GetProperty("usage")
                        .GetProperty("input_tokens").GetInt32();
                    break;

                case "content_block_delta":
                    if (root.GetProperty("delta").TryGetProperty("text", out var t))
                        yield return new ChatChunk(t.GetString(), null, false);
                    break;

                case "message_delta":
                    if (root.GetProperty("usage").TryGetProperty("output_tokens", out var o))
                        outputTokens = o.GetInt32();
                    break;

                case "message_stop":
                    yield return new ChatChunk(null, new TokenUsage(inputTokens, outputTokens), true);
                    yield break;
            }
        }
    }
}
