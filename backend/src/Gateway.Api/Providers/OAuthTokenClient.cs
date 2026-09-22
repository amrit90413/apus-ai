using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gateway.Api.Providers;

/// <summary>
/// OAuth 2.0 client settings for connecting a provider account by web login.
/// Disabled until ClientId, AuthorizeUrl, TokenUrl and RedirectUri are all set. Only
/// configure a client that Anthropic issued to your organization — reusing another
/// application's client id violates the provider's terms and gets the account blocked.
/// </summary>
public sealed class AnthropicOAuthOptions
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

    public bool Enabled =>
        !string.IsNullOrWhiteSpace(ClientId) &&
        Uri.IsWellFormedUriString(AuthorizeUrl, UriKind.Absolute) &&
        Uri.IsWellFormedUriString(TokenUrl, UriKind.Absolute) &&
        Uri.IsWellFormedUriString(RedirectUri, UriKind.Absolute);
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
    private readonly AnthropicOAuthOptions _opt;
    private readonly ILogger<OAuthTokenClient> _log;

    public OAuthTokenClient(IHttpClientFactory httpFactory, AnthropicOAuthOptions opt, ILogger<OAuthTokenClient> log)
    {
        _httpFactory = httpFactory; _opt = opt; _log = log;
    }

    public string BuildAuthorizeUrl(string state, string codeChallenge)
    {
        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = _opt.ClientId,
            ["redirect_uri"] = _opt.RedirectUri,
            ["scope"] = _opt.Scopes,
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
        };
        var qs = string.Join("&", query.Where(kv => !string.IsNullOrEmpty(kv.Value))
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}"));
        var sep = _opt.AuthorizeUrl.Contains('?') ? "&" : "?";
        return $"{_opt.AuthorizeUrl}{sep}{qs}";
    }

    public Task<OAuthTokenSet> ExchangeCodeAsync(string code, string codeVerifier, CancellationToken ct) =>
        PostAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = _opt.RedirectUri,
            ["code_verifier"] = codeVerifier,
        }, ct);

    public Task<OAuthTokenSet> RefreshAsync(string refreshToken, CancellationToken ct) =>
        PostAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        }, ct);

    private async Task<OAuthTokenSet> PostAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        form["client_id"] = _opt.ClientId!;
        if (!string.IsNullOrEmpty(_opt.ClientSecret)) form["client_secret"] = _opt.ClientSecret;

        using var req = new HttpRequestMessage(HttpMethod.Post, _opt.TokenUrl)
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
