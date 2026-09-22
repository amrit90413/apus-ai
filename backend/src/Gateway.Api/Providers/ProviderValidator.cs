using System.Net;
using Gateway.Api.Domain;
using Gateway.Api.Providers.Upstream;

namespace Gateway.Api.Providers;

/// <summary>
/// "Test connection": one cheap authenticated call per provider that proves the
/// stored credential works, without spending tokens.
/// </summary>
public interface IProviderValidator
{
    Task<(bool ok, string message)> ProbeAsync(ResolvedConnection connection, CancellationToken ct);
}

public sealed class ProviderValidator : IProviderValidator
{
    public const string HttpClientName = "provider-probe";

    private readonly IHttpClientFactory _httpFactory;
    private readonly GoogleServiceAccountTokens _googleTokens;
    private readonly AnthropicOptions _anthropic;
    private readonly ILogger<ProviderValidator> _log;

    public ProviderValidator(
        IHttpClientFactory httpFactory, GoogleServiceAccountTokens googleTokens,
        Microsoft.Extensions.Options.IOptions<AnthropicOptions> anthropic, ILogger<ProviderValidator> log)
    {
        _httpFactory = httpFactory; _googleTokens = googleTokens; _anthropic = anthropic.Value; _log = log;
    }

    public async Task<(bool ok, string message)> ProbeAsync(ResolvedConnection c, CancellationToken ct)
    {
        try
        {
            using var req = await BuildProbeAsync(c, ct);
            using var http = _httpFactory.CreateClient(HttpClientName);
            using var resp = await http.SendAsync(req, ct);
            return Interpret(c, resp.StatusCode);
        }
        catch (GoogleCredentialException ex)
        {
            return (false, ex.Message);
        }
        catch (NotSupportedException ex)
        {
            return (false, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning(ex, "Probe for {Provider} connection {Id} could not reach the provider.", c.Provider, c.ConnectionId);
            return (false, "Could not reach the provider. Check network egress and try again.");
        }
        catch (TaskCanceledException)
        {
            return (false, "The provider did not respond in time.");
        }
    }

    private async Task<HttpRequestMessage> BuildProbeAsync(ResolvedConnection c, CancellationToken ct)
    {
        switch (c.Provider)
        {
            case ProviderCatalog.Anthropic:
            {
                var req = new HttpRequestMessage(HttpMethod.Get, $"{ProviderEndpoints.RuntimeBase(c)}/v1/models?limit=1");
                req.Headers.Add("anthropic-version", _anthropic.Version);
                ProviderConnectionService.ApplyAuth(req, ProviderEndpoints.SchemeFor(c), c.Secret);
                return req;
            }

            case ProviderCatalog.OpenAi:
            {
                var req = new HttpRequestMessage(HttpMethod.Get, $"{ProviderEndpoints.RuntimeBase(c)}/v1/models");
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", c.Secret);
                return req;
            }

            case ProviderCatalog.Gemini:
            {
                // The key goes in a header, not the query string, so it cannot leak into
                // an access log or a proxy's URL capture.
                var req = new HttpRequestMessage(HttpMethod.Get, $"{ProviderEndpoints.RuntimeBase(c)}/v1beta/models?pageSize=1");
                req.Headers.Add("x-goog-api-key", c.Secret);
                return req;
            }

            case ProviderCatalog.Bedrock:
            {
                var req = new HttpRequestMessage(HttpMethod.Get,
                    $"{ProviderEndpoints.ControlBase(c)}/foundation-models?byOutputModality=TEXT");
                await SigV4Signer.SignAsync(req, "bedrock", ProviderEndpoints.Region(c),
                    c.Extra1("accessKeyId") ?? "", c.Secret, c.Extra1("sessionToken"), DateTimeOffset.UtcNow, ct);
                return req;
            }

            case ProviderCatalog.Vertex:
            {
                var token = await _googleTokens.GetAccessTokenAsync(c.ConnectionId, c.Secret, ct);
                var project = ProviderEndpoints.Project(c);
                var location = ProviderEndpoints.Location(c);
                var req = new HttpRequestMessage(HttpMethod.Get,
                    $"{ProviderEndpoints.RuntimeBase(c)}/v1/projects/{Uri.EscapeDataString(project)}/locations/{Uri.EscapeDataString(location)}/publishers/google/models?pageSize=1");
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                return req;
            }

            default:
                throw new NotSupportedException($"Testing a {c.Provider} connection is not supported.");
        }
    }

    private static (bool ok, string message) Interpret(ResolvedConnection c, HttpStatusCode status)
    {
        var name = ProviderCatalog.Find(c.Provider)?.DisplayName ?? c.Provider;

        if ((int)status is >= 200 and < 300)
            return (true, $"{name} accepted the credential.");

        return status switch
        {
            HttpStatusCode.Unauthorized =>
                (false, c.Type == ConnectionType.OAuth
                    ? $"{name} rejected the login. Reconnect the account."
                    : $"{name} rejected the credential. Check the key and connect again."),
            HttpStatusCode.Forbidden =>
                (false, $"The credential is recognised but not allowed to use {name}. Grant it model access and retry."),
            HttpStatusCode.NotFound =>
                (false, $"{name} did not recognise the configured endpoint. Check the region/project settings."),
            HttpStatusCode.TooManyRequests =>
                (false, $"{name} is rate limiting this account right now. The credential itself looks valid."),
            _ => (false, $"{name} returned {(int)status}."),
        };
    }
}
