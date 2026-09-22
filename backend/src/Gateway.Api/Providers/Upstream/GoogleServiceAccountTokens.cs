using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;

namespace Gateway.Api.Providers.Upstream;

/// <summary>The fields of a Google service-account key file the gateway needs.</summary>
public sealed record GoogleServiceAccount(
    [property: JsonPropertyName("client_email")] string? ClientEmail,
    [property: JsonPropertyName("private_key")] string? PrivateKey,
    [property: JsonPropertyName("private_key_id")] string? PrivateKeyId,
    [property: JsonPropertyName("token_uri")] string? TokenUri,
    [property: JsonPropertyName("project_id")] string? ProjectId)
{
    public bool IsUsable =>
        !string.IsNullOrWhiteSpace(ClientEmail) && !string.IsNullOrWhiteSpace(PrivateKey);

    public static GoogleServiceAccount? TryParse(string json)
    {
        try
        {
            var sa = JsonSerializer.Deserialize<GoogleServiceAccount>(json);
            return sa is { IsUsable: true } ? sa : null;
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>Raised when a Vertex service-account key is malformed or rejected by Google.</summary>
public sealed class GoogleCredentialException(string message) : Exception(message);

/// <summary>
/// Exchanges a Google service-account key for a short-lived OAuth access token
/// (RFC 7523 JWT bearer grant), which is the credential Vertex AI accepts.
///
/// Tokens are cached per connection until shortly before they expire, so a burst of
/// requests mints one token rather than one each.
/// </summary>
public sealed class GoogleServiceAccountTokens
{
    public const string HttpClientName = "google-token";
    private const string Scope = "https://www.googleapis.com/auth/cloud-platform";
    private const string DefaultTokenUri = "https://oauth2.googleapis.com/token";

    private readonly IHttpClientFactory _httpFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<GoogleServiceAccountTokens> _log;
    private readonly SemaphoreSlim _mintGate = new(1, 1);

    public GoogleServiceAccountTokens(IHttpClientFactory httpFactory, IMemoryCache cache, ILogger<GoogleServiceAccountTokens> log)
    {
        _httpFactory = httpFactory; _cache = cache; _log = log;
    }

    public async Task<string> GetAccessTokenAsync(Guid connectionId, string serviceAccountJson, CancellationToken ct)
    {
        var cacheKey = $"gcp-token:{connectionId}";
        if (_cache.TryGetValue(cacheKey, out string? cached) && cached is not null) return cached;

        await _mintGate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(cacheKey, out cached) && cached is not null) return cached;

            var sa = GoogleServiceAccount.TryParse(serviceAccountJson)
                ?? throw new GoogleCredentialException("The service-account key is not valid JSON with client_email and private_key.");

            var (token, expiresIn) = await MintAsync(sa, ct);
            // Refresh a minute early so an in-flight request never carries a token that
            // expires mid-call.
            var ttl = TimeSpan.FromSeconds(Math.Max(30, expiresIn - 60));
            _cache.Set(cacheKey, token, ttl);
            return token;
        }
        finally
        {
            _mintGate.Release();
        }
    }

    public void Invalidate(Guid connectionId) => _cache.Remove($"gcp-token:{connectionId}");

    private async Task<(string token, int expiresIn)> MintAsync(GoogleServiceAccount sa, CancellationToken ct)
    {
        var tokenUri = string.IsNullOrWhiteSpace(sa.TokenUri) ? DefaultTokenUri : sa.TokenUri!;
        var now = DateTimeOffset.UtcNow;

        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>
        {
            ["alg"] = "RS256", ["typ"] = "JWT",
        }));
        var claims = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["iss"] = sa.ClientEmail!,
            ["scope"] = Scope,
            ["aud"] = tokenUri,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.AddMinutes(30).ToUnixTimeSeconds(),
        }));

        using var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(sa.PrivateKey!.AsSpan());
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            throw new GoogleCredentialException("The service-account private key could not be read.");
        }

        var signingInput = $"{header}.{claims}";
        var signature = Base64Url(rsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        var assertion = $"{signingInput}.{signature}";

        using var req = new HttpRequestMessage(HttpMethod.Post, tokenUri)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["assertion"] = assertion,
            }),
        };

        using var http = _httpFactory.CreateClient(HttpClientName);
        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            // Google's error body can echo the assertion; log only the status.
            _log.LogWarning("Google token endpoint returned {Status} for service account {Email}.",
                (int)resp.StatusCode, sa.ClientEmail);
            throw new GoogleCredentialException(
                $"Google rejected the service-account key ({(int)resp.StatusCode}). Check that the key is active and has Vertex AI access.");
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var token = doc.RootElement.TryGetProperty("access_token", out var t) ? t.GetString() : null;
            var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var ev) ? ev : 3600;
            if (string.IsNullOrEmpty(token))
                throw new GoogleCredentialException("Google returned no access_token.");
            return (token, expiresIn);
        }
        catch (JsonException)
        {
            throw new GoogleCredentialException("Google returned an unreadable token response.");
        }
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
