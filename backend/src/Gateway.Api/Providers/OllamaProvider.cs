using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Gateway.Api.Providers;

public sealed class OllamaOptions
{
    public string BaseUrl { get; set; } = "http://host.docker.internal:11434";
}

/// <summary>
/// Ollama provider — routes to a local Ollama instance (free, no API key).
/// Supports any model pulled via `ollama pull`, e.g. llama3.2, mistral, gemma2.
/// Uses Ollama's /api/chat streaming endpoint which emits newline-delimited JSON.
/// </summary>
public sealed class OllamaProvider : IAiProvider
{
    private readonly HttpClient _http;
    private readonly OllamaOptions _opt;

    public OllamaProvider(HttpClient http, OllamaOptions opt)
    {
        _http = http;
        _opt = opt;
    }

    public string Name => "ollama";

    // Supports any model that starts with a known open-source prefix
    public bool Supports(string model) =>
        model.StartsWith("llama", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("mistral", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("gemma", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("phi", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("qwen", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("deepseek", StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<ChatChunk> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var messages = request.Messages
            .Select(m => new { role = m.Role == "system" ? "system" : m.Role, content = m.Content })
            .ToList();

        var body = new
        {
            model = request.Model,
            messages,
            stream = true,
            options = new { num_predict = request.MaxTokens }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_opt.BaseUrl}/api/chat");
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        int inputTokens = 0, outputTokens = 0;
        string? line;

        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            // Each chunk: { "message": { "content": "..." }, "done": false }
            if (root.TryGetProperty("message", out var msg) &&
                msg.TryGetProperty("content", out var content))
            {
                var text = content.GetString();
                if (!string.IsNullOrEmpty(text))
                    yield return new ChatChunk(text, null, false);
            }

            // Final chunk has done=true and eval_count (output tokens)
            if (root.TryGetProperty("done", out var done) && done.GetBoolean())
            {
                if (root.TryGetProperty("prompt_eval_count", out var pt)) inputTokens = pt.GetInt32();
                if (root.TryGetProperty("eval_count", out var ot)) outputTokens = ot.GetInt32();
                yield return new ChatChunk(null, new TokenUsage(inputTokens, outputTokens), true);
                yield break;
            }
        }
    }
}
