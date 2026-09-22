using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gateway.Api.Billing;

namespace Gateway.Api.Providers.Upstream;

/// <summary>
/// Claude (and other Anthropic-protocol models) on AWS Bedrock.
///
/// Bedrock takes the Anthropic Messages body but supplies the model in the URL, names
/// the protocol version `bedrock-2023-05-31`, and answers streaming requests with AWS
/// event-stream framing instead of SSE — so this adapter rewrites the request and
/// re-frames the response into the SSE the client is waiting for.
/// </summary>
public sealed class BedrockUpstreamAdapter : IUpstreamAdapter
{
    private const string BedrockAnthropicVersion = "bedrock-2023-05-31";

    private readonly ILogger<BedrockUpstreamAdapter> _log;

    public BedrockUpstreamAdapter(ILogger<BedrockUpstreamAdapter> log) => _log = log;

    public string Provider => ProviderCatalog.Bedrock;

    public async Task<HttpRequestMessage> BuildAsync(UpstreamCall call, CancellationToken ct)
    {
        var native = ModelMapping.Resolve(call.Connection, call.CanonicalModel);
        var body = (JsonObject)call.Body.DeepClone();

        // Bedrock rejects these: the model is part of the path and streaming is chosen
        // by the operation, not a body flag.
        body.Remove("model");
        body.Remove("stream");
        body["anthropic_version"] = BedrockAnthropicVersion;

        var operation = call.Streaming ? "invoke-with-response-stream" : "invoke";
        var url = $"{ProviderEndpoints.RuntimeBase(call.Connection)}/model/{Uri.EscapeDataString(native)}/{operation}";

        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body)),
        };
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        req.Headers.Accept.ParseAdd(call.Streaming ? "application/vnd.amazon.eventstream" : "application/json");

        await SigV4Signer.SignAsync(req, "bedrock", ProviderEndpoints.Region(call.Connection),
            call.Connection.Extra1("accessKeyId") ?? "", call.Connection.Secret,
            call.Connection.Extra1("sessionToken"), DateTimeOffset.UtcNow, ct);

        return req;
    }

    public async Task<BilledTokens> RelayAsync(UpstreamCall call, HttpResponseMessage response, IResponseSink sink, CancellationToken ct)
    {
        var usage = new UsageAccumulator();

        if (!response.IsSuccessStatusCode)
        {
            await RelayErrorAsync(response, sink, ct);
            return BilledTokens.None;
        }

        if (!call.Streaming)
        {
            // Bedrock's non-streaming body already is an Anthropic message object.
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            usage.ApplyResponseBody(bytes);
            sink.BeginJson(200);
            await sink.WriteAsync(bytes, ct);
            return usage.ToBilled();
        }

        sink.BeginEventStream();
        await using var upstream = await response.Content.ReadAsStreamAsync(ct);

        await foreach (var frame in AwsEventStreamReader.ReadAsync(upstream, ct))
        {
            if (frame.ExceptionType is not null)
            {
                // Mid-stream failure. The client has already had a 200, so the only
                // honest signal left is an Anthropic `error` event.
                _log.LogWarning("Bedrock stream raised {Exception} for model {Model}", frame.ExceptionType, call.CanonicalModel);
                await sink.WriteAsync(Sse.Event("error", JsonSerializer.Serialize(new
                {
                    type = "error",
                    error = new { type = "api_error", message = "The provider ended the stream early." },
                })), ct);
                await sink.FlushAsync(ct);
                break;
            }

            var inner = AwsEventStreamReader.UnwrapChunk(frame.Payload);
            if (inner is null) continue;

            var json = Encoding.UTF8.GetString(inner);
            var type = EventType(json);
            if (type is null) continue;

            usage.ApplySseLine("data: " + json);
            await sink.WriteAsync(Sse.Event(type, json), ct);
            await sink.FlushAsync(ct);
        }

        return usage.ToBilled();
    }

    /// <summary>Bedrock chunks carry the Anthropic event name inside the JSON, not in a header.</summary>
    private static string? EventType(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Translates a Bedrock error into the Anthropic error shape. The upstream body is
    /// not forwarded verbatim: it can name the AWS account and role.
    /// </summary>
    internal static async Task RelayErrorAsync(HttpResponseMessage response, IResponseSink sink, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        var (type, message) = status switch
        {
            400 => ("invalid_request_error", "The provider rejected the request."),
            403 or 401 => ("authentication_error", "The organization's provider credential was rejected."),
            404 => ("not_found_error", "The provider does not offer this model in the configured region."),
            429 => ("rate_limit_error", "The provider is rate limiting this account."),
            _ => ("api_error", "The provider could not complete the request."),
        };

        sink.BeginJson(status);
        await sink.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            type = "error",
            error = new { type, message },
        })), ct);
    }
}
