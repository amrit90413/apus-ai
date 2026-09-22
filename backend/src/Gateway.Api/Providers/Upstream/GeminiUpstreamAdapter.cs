using Gateway.Api.Billing;

namespace Gateway.Api.Providers.Upstream;

/// <summary>
/// Google's direct Gemini API. The protocol translation lives in
/// <see cref="GeminiTranslation"/> because Vertex serves the same shape under a
/// different URL and credential.
/// </summary>
public sealed class GeminiUpstreamAdapter : IUpstreamAdapter
{
    private readonly GeminiTranslation _translation;

    public GeminiUpstreamAdapter(GeminiTranslation translation) => _translation = translation;

    public string Provider => ProviderCatalog.Gemini;

    public Task<HttpRequestMessage> BuildAsync(UpstreamCall call, CancellationToken ct)
    {
        var native = ModelMapping.Resolve(call.Connection, call.CanonicalModel);
        var operation = call.Streaming ? "streamGenerateContent?alt=sse" : "generateContent";
        var url = $"{ProviderEndpoints.RuntimeBase(call.Connection)}/v1beta/models/{Uri.EscapeDataString(native)}:{operation}";

        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = GeminiTranslation.BuildRequestContent(call.Body),
        };
        // Header rather than the documented ?key= query parameter: a key in a URL ends
        // up in access logs and proxy traces.
        req.Headers.Add("x-goog-api-key", call.Connection.Secret);
        return Task.FromResult(req);
    }

    public Task<BilledTokens> RelayAsync(UpstreamCall call, HttpResponseMessage response, IResponseSink sink, CancellationToken ct) =>
        _translation.RelayAsync(call, response, sink, ct);
}
