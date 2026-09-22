using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Gateway.Api.Providers;

public sealed class AnthropicOptions
{
    public string ApiKey { get; set; } = "";       // last-resort fallback from env/K8s secret — never leaves the server
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
    private readonly IProviderConnectionService _connections;

    public AnthropicProvider(HttpClient http, IOptions<AnthropicOptions> opt, IProviderConnectionService connections)
    {
        _http = http;
        _opt = opt.Value;
        _connections = connections;
    }

    public string Name => "anthropic";
    public bool Supports(string model) => model.StartsWith("claude", StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<ChatChunk> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Org connection (API key or OAuth) > platform connection > env var. Stored
        // connections win so admins can rotate without redeploying.
        var connection = await _connections.ResolveAsync(request.OrganizationId, Name, ct);
        var auth = connection is not null
            ? new ProviderAuth(ProviderEndpoints.SchemeFor(connection), connection.Secret, connection.ConnectionId)
            : string.IsNullOrWhiteSpace(_opt.ApiKey) ? null : new ProviderAuth(AuthScheme.ApiKey, _opt.ApiKey, Guid.Empty);
        if (auth is null) throw new ProviderNotConfiguredException(Name);

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
        ProviderConnectionService.ApplyAuth(req, auth.Scheme, auth.Secret);
        req.Headers.Add("anthropic-version", _opt.Version);
        req.Content = JsonContent.Create(body);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // Revoked/rotated upstream. Record it against the connection and drop the
            // cached auth so the next request re-reads instead of failing for the cache
            // lifetime.
            await _connections.ReportFailureAsync(auth.CredentialId, (int)resp.StatusCode, "provider rejected the credential", CancellationToken.None);
            _connections.Invalidate(request.OrganizationId, Name);
            throw new ProviderAuthException(Name, (int)resp.StatusCode);
        }
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
                    // Cache reads/writes are billed input tokens too.
                    var usage = root.GetProperty("message").GetProperty("usage");
                    inputTokens = usage.GetProperty("input_tokens").GetInt32()
                        + (usage.TryGetProperty("cache_creation_input_tokens", out var cc) && cc.TryGetInt32(out var ccv) ? ccv : 0)
                        + (usage.TryGetProperty("cache_read_input_tokens", out var cr) && cr.TryGetInt32(out var crv) ? crv : 0);
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
