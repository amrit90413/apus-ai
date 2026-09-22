using System.Text.Json;
using System.Text.Json.Nodes;
using Gateway.Api.Billing;

namespace Gateway.Api.Providers.Upstream;

/// <summary>
/// Claude and Gemini on Google Vertex AI.
///
/// Vertex serves Anthropic models over the Anthropic protocol (`:streamRawPredict`,
/// protocol version `vertex-2023-10-16`, model in the path) and Gemini models over
/// Google's own protocol — so this adapter routes on the model family and reuses the
/// Gemini translation for the latter.
/// </summary>
public sealed class VertexUpstreamAdapter : IUpstreamAdapter
{
    private const string VertexAnthropicVersion = "vertex-2023-10-16";

    private readonly GoogleServiceAccountTokens _tokens;
    private readonly GeminiTranslation _gemini;

    public VertexUpstreamAdapter(GoogleServiceAccountTokens tokens, GeminiTranslation gemini)
    {
        _tokens = tokens; _gemini = gemini;
    }

    public string Provider => ProviderCatalog.Vertex;

    private static bool IsGemini(string model) => model.StartsWith("gemini", StringComparison.OrdinalIgnoreCase);

    public async Task<HttpRequestMessage> BuildAsync(UpstreamCall call, CancellationToken ct)
    {
        var connection = call.Connection;
        var token = await _tokens.GetAccessTokenAsync(connection.ConnectionId, connection.Secret, ct);
        var native = ModelMapping.Resolve(connection, call.CanonicalModel);
        var project = ProviderEndpoints.Project(connection);
        var location = ProviderEndpoints.Location(connection);
        var basePath = $"{ProviderEndpoints.RuntimeBase(connection)}/v1/projects/{Uri.EscapeDataString(project)}/locations/{Uri.EscapeDataString(location)}/publishers";

        HttpRequestMessage req;
        if (IsGemini(call.CanonicalModel))
        {
            var op = call.Streaming ? "streamGenerateContent?alt=sse" : "generateContent";
            var url = $"{basePath}/google/models/{Uri.EscapeDataString(native)}:{op}";
            req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = GeminiTranslation.BuildRequestContent(call.Body),
            };
        }
        else
        {
            var body = (JsonObject)call.Body.DeepClone();
            body.Remove("model");                       // the model is in the path
            body["anthropic_version"] = VertexAnthropicVersion;

            var op = call.Streaming ? "streamRawPredict" : "rawPredict";
            var url = $"{basePath}/anthropic/models/{Uri.EscapeDataString(native)}:{op}";
            req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body)),
            };
            req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    public async Task<BilledTokens> RelayAsync(UpstreamCall call, HttpResponseMessage response, IResponseSink sink, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            await BedrockUpstreamAdapter.RelayErrorAsync(response, sink, ct);
            return BilledTokens.None;
        }

        // Anthropic-on-Vertex already speaks Anthropic SSE, so it needs no translation.
        return IsGemini(call.CanonicalModel)
            ? await _gemini.RelayAsync(call, response, sink, ct)
            : await AnthropicUpstreamAdapter.RelayPassthroughAsync(response, sink, ct);
    }
}
