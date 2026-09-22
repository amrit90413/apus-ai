using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gateway.Api.Billing;

namespace Gateway.Api.Providers.Upstream;

/// <summary>
/// One upstream call, described in Anthropic Messages terms. The gateway's public
/// surface is Anthropic-compatible, so that is the internal lingua franca: adapters
/// translate to and from their provider's own shape.
/// </summary>
public sealed record UpstreamCall(
    ResolvedConnection Connection,
    /// <summary>The canonical APUS model id, e.g. claude-sonnet-5. Used for pricing and the ledger.</summary>
    string CanonicalModel,
    JsonObject Body,
    byte[] RawBody,
    bool Streaming,
    string? AnthropicVersion,
    IReadOnlyList<string> AnthropicBetas);

/// <summary>
/// Where an adapter writes the client-facing response. Wraps HttpResponse so an adapter
/// cannot accidentally reach for anything else on the request, and so tests can capture
/// output without a server.
/// </summary>
public interface IResponseSink
{
    bool Started { get; }
    void BeginJson(int status);
    void BeginEventStream();
    void SetHeader(string name, string value);
    Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct);
    Task FlushAsync(CancellationToken ct);
}

public sealed class HttpResponseSink : IResponseSink
{
    private readonly HttpResponse _response;

    public HttpResponseSink(HttpResponse response) => _response = response;

    public bool Started => _response.HasStarted;

    public void BeginJson(int status)
    {
        _response.StatusCode = status;
        _response.ContentType = "application/json";
    }

    public void BeginEventStream()
    {
        _response.StatusCode = 200;
        _response.ContentType = "text/event-stream";
        _response.Headers.CacheControl = "no-cache";
        // NGINX buffers proxied responses by default, which would hold tokens back
        // until the generation finished.
        _response.Headers["X-Accel-Buffering"] = "no";
    }

    public void SetHeader(string name, string value)
    {
        if (!_response.HasStarted) _response.Headers[name] = value;
    }

    public Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct) =>
        _response.Body.WriteAsync(bytes, ct).AsTask();

    public Task FlushAsync(CancellationToken ct) => _response.Body.FlushAsync(ct);
}

/// <summary>Collects a response in memory. Used by tests and by the non-streaming relay path.</summary>
public sealed class BufferedResponseSink : IResponseSink
{
    private readonly MemoryStream _buffer = new();

    public bool Started { get; private set; }
    public int Status { get; private set; } = 200;
    public string ContentType { get; private set; } = "application/json";
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void BeginJson(int status) { Status = status; ContentType = "application/json"; Started = true; }
    public void BeginEventStream() { Status = 200; ContentType = "text/event-stream"; Started = true; }
    public void SetHeader(string name, string value) => Headers[name] = value;

    public async Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        Started = true;
        await _buffer.WriteAsync(bytes, ct);
    }

    public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;

    public string Text => Encoding.UTF8.GetString(_buffer.ToArray());
    public byte[] Bytes => _buffer.ToArray();
}

/// <summary>
/// An upstream provider's wire protocol: how to build its request and how to turn its
/// response back into the Anthropic-shaped stream the client expects.
///
/// Adding a provider means adding one of these. Nothing in the quota, allowance,
/// billing or user systems changes.
/// </summary>
public interface IUpstreamAdapter
{
    string Provider { get; }

    /// <summary>Builds the signed/authenticated upstream request.</summary>
    Task<HttpRequestMessage> BuildAsync(UpstreamCall call, CancellationToken ct);

    /// <summary>
    /// Relays the upstream response to the client in Anthropic shape and returns the
    /// tokens actually billed. Must not throw for an upstream error status — it relays
    /// the error and returns zero usage.
    /// </summary>
    Task<BilledTokens> RelayAsync(UpstreamCall call, HttpResponseMessage response, IResponseSink sink, CancellationToken ct);

    /// <summary>Provider-native id for a canonical model, honouring a per-connection override map.</summary>
    string NativeModelId(ResolvedConnection connection, string canonicalModel) =>
        ModelMapping.Resolve(connection, canonicalModel);
}

/// <summary>
/// Canonical model id to provider-native id.
///
/// Cloud resellers rename models (Bedrock's `anthropic.claude-…-v1:0`, Vertex's
/// `claude-…@date`), and those ids change faster than a release cycle, so the mapping
/// is per-connection configuration rather than a hardcoded table: set `modelMap` on the
/// connection to a JSON object of canonical → native. Unmapped ids pass through.
/// </summary>
public static class ModelMapping
{
    public static string Resolve(ResolvedConnection connection, string canonicalModel)
    {
        var raw = connection.Config1("modelMap");
        if (string.IsNullOrWhiteSpace(raw)) return canonicalModel;

        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(raw);
            if (map is not null && map.TryGetValue(canonicalModel, out var native) && !string.IsNullOrWhiteSpace(native))
                return native;
        }
        catch (JsonException) { /* a malformed map must not break routing */ }

        return canonicalModel;
    }
}

/// <summary>Helpers shared by the SSE-producing adapters.</summary>
public static class Sse
{
    public static byte[] Event(string type, string json) =>
        Encoding.UTF8.GetBytes($"event: {type}\ndata: {json}\n\n");

    public static byte[] Event(string type, JsonNode node) => Event(type, node.ToJsonString());
}
