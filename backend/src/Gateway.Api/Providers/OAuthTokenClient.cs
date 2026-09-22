using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gateway.Api.Providers;

/// <summary>
/// OAuth 2.0 client settings for connecting a provider account by web login.
/// Disabled until ClientId, AuthorizeUrl, TokenUrl and RedirectUri are all set. Only
/// configure a client the provider issued to your organization — reusing another
/// application's client id violates the provider's terms and gets the account blocked.
///
/// One instance per provider; see ProviderOAuthRegistry.
/// </summary>
public class ProviderOAuthOptions
{
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }          // omit for public (PKCE-only) clients
    public string AuthorizeUrl { get; set; } = "";
    public string TokenUrl { get; set; } = "";
    public string Scopes { get; set; } = "";
    /// <summary>Must match the URL registered with the provider, e.g. https://ai.example.com/admin/providers/callback</summary>
    public string RedirectUri { get; set; } = "";
    /// <summary>Refresh the access token this many seconds before it expires.</summary>
    public int RefreshSkewSeconds { get; set; } = 120;

    /// <summary>
    /// RFC 7009 revocation endpoint. Optional: when set, disconnecting tells the
    /// provider to invalidate the grant instead of only forgetting it locally.
    /// </summary>
    public string? RevokeUrl { get; set; }

    /// <summary>
    /// Relative dashboard path the admin lands on after the redirect flow completes.
    /// Relative by design — an absolute value here would be an open redirect.
    /// </summary>
    public string CompletionPath { get; set; } = "/settings/ai-providers";

    public bool Enabled =>
        !string.IsNullOrWhiteSpace(ClientId) &&
        Uri.IsWellFormedUriString(AuthorizeUrl, UriKind.Absolute) &&
        Uri.IsWellFormedUriString(TokenUrl, UriKind.Absolute) &&
        Uri.IsWellFormedUriString(RedirectUri, UriKind.Absolute);
}

/// <summary>
/// The legacy `Anthropic:OAuth` configuration section. Kept as its own type so
/// existing deployments and DI registrations keep working; `Providers:OAuth:anthropic`
/// is the general form and takes precedence when both are set.
/// </summary>
public sealed class AnthropicOAuthOptions : ProviderOAuthOptions
{
}

public sealed record OAuthTokenSet(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset? ExpiresAt,
    string? Scope);

/// <summary>Raised when the provider rejects the grant itself (invalid_grant); the stored credential is dead.</summary>
public sealed class OAuthGrantRejectedException(string message) : Exception(message);

/// <summary>
/// Talks to the provider's token endpoint. Authorization-code + PKCE exchange and the
/// refresh-token grant, both standard RFC 6749 form posts. Transient failures surface
/// as HttpRequestException so callers can keep serving a still-valid token.
/// </summary>
public sealed class OAuthTokenClient
{
    public const string HttpClientName = "oauth-token";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ProviderOAuthOptions _opt;
    private readonly ILogger<OAuthTokenClient> _log;

    public OAuthTokenClient(IHttpClientFactory httpFactory, AnthropicOAuthOptions opt, ILogger<OAuthTokenClient> log)
    {
        _httpFactory = httpFactory; _opt = opt; _log = log;
    }

    public string BuildAuthorizeUrl(string state, string codeChallenge) =>
        BuildAuthorizeUrl(_opt, state, codeChallenge);

    public Task<OAuthTokenSet> ExchangeCodeAsync(ProviderOAuthOptions opt, string code, string codeVerifier, CancellationToken ct) =>
        PostAsync(opt, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = opt.RedirectUri,
            ["code_verifier"] = codeVerifier,
        }, ct);

    public Task<OAuthTokenSet> RefreshAsync(ProviderOAuthOptions opt, string refreshToken, CancellationToken ct) =>
        PostAsync(opt, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        }, ct);

    public static string BuildAuthorizeUrl(ProviderOAuthOptions opt, string state, string codeChallenge)
    {
        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = opt.ClientId,
            ["redirect_uri"] = opt.RedirectUri,
            ["scope"] = opt.Scopes,
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
        };
        var qs = string.Join("&", query.Where(kv => !string.IsNullOrEmpty(kv.Value))
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}"));
        var sep = opt.AuthorizeUrl.Contains('?') ? "&" : "?";
        return $"{opt.AuthorizeUrl}{sep}{qs}";
    }

    public Task<OAuthTokenSet> ExchangeCodeAsync(string code, string codeVerifier, CancellationToken ct) =>
        ExchangeCodeAsync(_opt, code, codeVerifier, ct);

    public Task<OAuthTokenSet> RefreshAsync(string refreshToken, CancellationToken ct) =>
        RefreshAsync(_opt, refreshToken, ct);

    /// <summary>
    /// RFC 7009 token revocation. Best effort by design: the spec says a server may
    /// answer 200 for an already-invalid token, and a provider that is down must not
    /// stop an admin from disconnecting locally.
    /// </summary>
    public async Task RevokeAsync(ProviderOAuthOptions opt, string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(opt.RevokeUrl)) return;

        var form = new Dictionary<string, string>
        {
            ["token"] = token,
            ["client_id"] = opt.ClientId!,
        };
        if (!string.IsNullOrEmpty(opt.ClientSecret)) form["client_secret"] = opt.ClientSecret;

        using var req = new HttpRequestMessage(HttpMethod.Post, opt.RevokeUrl) { Content = new FormUrlEncodedContent(form) };
        using var http = _httpFactory.CreateClient(HttpClientName);
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            _log.LogWarning("Token revocation returned {Status}.", (int)resp.StatusCode);
    }

    private async Task<OAuthTokenSet> PostAsync(ProviderOAuthOptions opt, Dictionary<string, string> form, CancellationToken ct)
    {
        form["client_id"] = opt.ClientId!;
        if (!string.IsNullOrEmpty(opt.ClientSecret)) form["client_secret"] = opt.ClientSecret;

        using var req = new HttpRequestMessage(HttpMethod.Post, opt.TokenUrl)
        {
            Content = new FormUrlEncodedContent(form)
        };
        req.Headers.Accept.ParseAdd("application/json");

        using var http = _httpFactory.CreateClient(HttpClientName);
        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (resp.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
        {
            // RFC 6749 §5.2: invalid_grant / invalid_client are terminal for this credential.
            var err = TryParse<TokenError>(body);
            _log.LogWarning("OAuth {Grant} rejected: {Error}", form["grant_type"], err?.Error ?? ((int)resp.StatusCode).ToString());
            throw new OAuthGrantRejectedException(err?.Error ?? $"token endpoint returned {(int)resp.StatusCode}");
        }
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Token endpoint returned {(int)resp.StatusCode}.", null, resp.StatusCode);

        var tok = TryParse<TokenResponse>(body);
        if (tok?.AccessToken is null or "")
            throw new HttpRequestException("Token endpoint returned no access_token.");

        var expires = tok.ExpiresIn is > 0 ? DateTimeOffset.UtcNow.AddSeconds(tok.ExpiresIn.Value) : (DateTimeOffset?)null;
        return new OAuthTokenSet(tok.AccessToken, tok.RefreshToken, expires, tok.Scope);
    }

    private static T? TryParse<T>(string json)
    {
        try { return JsonSerializer.Deserialize<T>(json); }
        catch (JsonException) { return default; }
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int? ExpiresIn,
        [property: JsonPropertyName("scope")] string? Scope);

    private sealed record TokenError(
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("error_description")] string? Description);
}
