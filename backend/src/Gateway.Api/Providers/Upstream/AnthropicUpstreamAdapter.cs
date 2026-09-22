using System.Text;
using System.Text.Json;
using Gateway.Api.Billing;
using Microsoft.Extensions.Options;

namespace Gateway.Api.Providers.Upstream;

/// <summary>
/// Collects the authoritative token counts from Anthropic `usage` objects. For a
/// stream, message_start carries the input side and message_delta the output side
/// (newer API versions repeat the input fields there too — later values win).
/// Cache reads and writes are billed tokens, so they count.
/// </summary>
public sealed class UsageAccumulator
{
    public int Input { get; private set; }
    public int CacheCreation { get; private set; }
    public int CacheRead { get; private set; }
    public int Output { get; private set; }

    public int InputTotal => Input + CacheCreation + CacheRead;
    public long Total => (long)InputTotal + Output;

    public BilledTokens ToBilled() => new(Input, Output, CacheRead, CacheCreation);

    public void Set(int input, int output, int cacheRead = 0, int cacheWrite = 0)
    {
        Input = input; Output = output; CacheRead = cacheRead; CacheCreation = cacheWrite;
    }

    public void Apply(JsonElement usage)
    {
        if (usage.ValueKind != JsonValueKind.Object) return;
        if (usage.TryGetProperty("input_tokens", out var i) && i.TryGetInt32(out var iv)) Input = iv;
        if (usage.TryGetProperty("cache_creation_input_tokens", out var c) && c.TryGetInt32(out var cv)) CacheCreation = cv;
        if (usage.TryGetProperty("cache_read_input_tokens", out var r) && r.TryGetInt32(out var rv)) CacheRead = rv;
        if (usage.TryGetProperty("output_tokens", out var o) && o.TryGetInt32(out var ov)) Output = ov;
    }

    public void ApplySseLine(string line)
    {
        if (!line.StartsWith("data:", StringComparison.Ordinal) || !line.Contains("\"usage\"", StringComparison.Ordinal)) return;
        var json = line["data:".Length..].Trim();
        if (json.Length == 0 || json[0] != '{') return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            switch (type)
            {
                case "message_start":
                    if (root.TryGetProperty("message", out var msg) && msg.TryGetProperty("usage", out var mu)) Apply(mu);
                    break;
                case "message_delta":
                    if (root.TryGetProperty("usage", out var du)) Apply(du);
                    break;
            }
        }
        catch (JsonException) { /* partial or non-JSON data line; ignore */ }
    }

    public void ApplyResponseBody(byte[] bytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.TryGetProperty("usage", out var u)) Apply(u);
        }
        catch (JsonException) { /* relay anyway; usage stays 0 and the estimate is refunded */ }
    }
}

/// <summary>
/// Anthropic's own Messages API. The client already speaks this protocol, so the body
/// is forwarded byte-for-byte and the response — SSE or JSON — is streamed straight
/// back, with the `usage` blocks tee-parsed on the way past for accounting.
///
/// Nothing is buffered beyond one partial SSE line, so time-to-first-token is
/// unaffected by the gateway sitting in the path.
/// </summary>
public sealed class AnthropicUpstreamAdapter : IUpstreamAdapter
{
    private readonly AnthropicOptions _opt;

    public AnthropicUpstreamAdapter(IOptions<AnthropicOptions> opt) => _opt = opt.Value;

    public string Provider => ProviderCatalog.Anthropic;

    public Task<HttpRequestMessage> BuildAsync(UpstreamCall call, CancellationToken ct)
    {
        var body = call.RawBody;

        // A per-connection model map can rename the model even on the direct API
        // (an enterprise alias, say); only re-serialize when it actually changes.
        var native = ModelMapping.Resolve(call.Connection, call.CanonicalModel);
        if (!string.Equals(native, call.Body["model"]?.GetValue<string>(), StringComparison.Ordinal))
        {
            call.Body["model"] = native;
            body = JsonSerializer.SerializeToUtf8Bytes(call.Body);
        }

        var req = new HttpRequestMessage(HttpMethod.Post, $"{ProviderEndpoints.RuntimeBase(call.Connection)}/v1/messages")
        {
            Content = new ByteArrayContent(body),
        };
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        ProviderConnectionService.ApplyAuth(req, ProviderEndpoints.SchemeFor(call.Connection), call.Connection.Secret);
        req.Headers.Add("anthropic-version", call.AnthropicVersion ?? _opt.Version);

        // Client-requested betas ride along; ApplyAuth may already have added
        // oauth-2025-04-20, so skip a duplicate.
        foreach (var beta in call.AnthropicBetas.Where(b => !string.IsNullOrWhiteSpace(b)))
            if (!req.Headers.TryGetValues("anthropic-beta", out var existing) || !existing.Contains(beta))
                req.Headers.Add("anthropic-beta", beta);

        return Task.FromResult(req);
    }

    public Task<BilledTokens> RelayAsync(UpstreamCall call, HttpResponseMessage response, IResponseSink sink, CancellationToken ct) =>
        RelayPassthroughAsync(response, sink, ct);

    /// <summary>
    /// Shared by every adapter whose upstream already speaks Anthropic SSE (direct API
    /// and Vertex). Copies bytes through while tee-parsing `usage`.
    /// </summary>
    internal static async Task<BilledTokens> RelayPassthroughAsync(HttpResponseMessage response, IResponseSink sink, CancellationToken ct)
    {
        var usage = new UsageAccumulator();
        var isStream = response.Content.Headers.ContentType?.MediaType?
            .Equals("text/event-stream", StringComparison.OrdinalIgnoreCase) == true;

        CopyIdentifyingHeaders(response, sink);

        if (!isStream)
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            usage.ApplyResponseBody(bytes);
            sink.BeginJson((int)response.StatusCode);
            await sink.WriteAsync(bytes, ct);
            return usage.ToBilled();
        }

        sink.BeginEventStream();
        await using var upstream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[16 * 1024];
        var line = new StringBuilder();

        int read;
        while ((read = await upstream.ReadAsync(buffer, ct)) > 0)
        {
            await sink.WriteAsync(buffer.AsMemory(0, read), ct);
            await sink.FlushAsync(ct);

            var text = Encoding.UTF8.GetString(buffer, 0, read);
            foreach (var ch in text)
            {
                if (ch != '\n') { line.Append(ch); continue; }
                usage.ApplySseLine(line.ToString());
                line.Clear();
            }
        }
        if (line.Length > 0) usage.ApplySseLine(line.ToString());

        return usage.ToBilled();
    }

    /// <summary>
    /// Forwards the handful of upstream headers a client can act on. Deliberately not a
    /// blanket copy: provider headers can carry account identifiers.
    /// </summary>
    internal static void CopyIdentifyingHeaders(HttpResponseMessage response, IResponseSink sink)
    {
        if (response.Headers.TryGetValues("request-id", out var rid)) sink.SetHeader("X-Upstream-Request-Id", rid.First());
        if (response.Headers.TryGetValues("anthropic-ratelimit-requests-remaining", out var rr))
            sink.SetHeader("anthropic-ratelimit-requests-remaining", rr.First());
    }
}
