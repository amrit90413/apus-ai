using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Gateway.Api.Domain;
using Gateway.Api.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Gateway.Api.Auth;

/// <summary>Personal key helpers: generation, hashing, display prefix.</summary>
public static class ApiKeys
{
    public const string Scheme = "ApiKey";
    public const string KeyPrefix = "apus_";

    public static string Generate() =>
        KeyPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public static string DisplayPrefix(string key) => key.Length >= 12 ? key[..12] : key;

    public static bool LooksLikeKey(string? value) =>
        value is not null && value.StartsWith(KeyPrefix, StringComparison.Ordinal) && value.Length is >= 40 and <= 128;
}

/// <summary>
/// Authenticates the Anthropic-compatible proxy with a personal key passed as
/// x-api-key or Authorization: Bearer apus_... . The resulting principal carries the
/// same claims as a JWT session (user, org, workspace, role) so quota, balance, tenant
/// scoping and audit logs work unchanged; session_id is the key id.
/// Lookups are cached for 30s, which bounds revocation latency.
/// </summary>
public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LastUsedWriteInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMemoryCache _cache;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
        IServiceScopeFactory scopeFactory, IMemoryCache cache)
        : base(options, logger, encoder)
    {
        _scopeFactory = scopeFactory; _cache = cache;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var key = ExtractKey();
        if (key is null) return AuthenticateResult.NoResult();

        var hash = ApiKeys.Hash(key);
        var cacheKey = $"apikey:{hash}";

        if (!_cache.TryGetValue(cacheKey, out KeyIdentity? identity))
        {
            identity = await LoadAsync(hash);
            // Cache misses too (as null) so a flood of bad keys doesn't hammer Postgres.
            _cache.Set(cacheKey, identity, CacheTtl);
        }

        if (identity is null)
            return AuthenticateResult.Fail("Invalid or revoked API key.");

        await TouchLastUsedAsync(identity.KeyId);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, identity.UserId.ToString()),
            new("org_id", identity.OrganizationId.ToString()),
            new("workspace_id", identity.WorkspaceId.ToString()),
            new("session_id", identity.KeyId.ToString()),
            new(ClaimTypes.Role, identity.Role.ToString()),
            new(JwtRegisteredClaimNames.Email, identity.Email),
            new("auth_kind", "api_key"),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name, ClaimTypes.NameIdentifier, ClaimTypes.Role));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        // Anthropic-shaped error so IDE clients render it sensibly.
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.ContentType = "application/json";
        return Response.WriteAsync(
            """{"type":"error","error":{"type":"authentication_error","message":"Invalid or revoked API key. Run `npx apus-ai` to reconnect."}}""");
    }

    private string? ExtractKey()
    {
        var xApiKey = Request.Headers["x-api-key"].FirstOrDefault();
        if (ApiKeys.LooksLikeKey(xApiKey)) return xApiKey;

        var auth = Request.Headers.Authorization.FirstOrDefault();
        if (auth is not null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = auth["Bearer ".Length..].Trim();
            if (ApiKeys.LooksLikeKey(token)) return token;
        }
        return null;
    }

    private async Task<KeyIdentity?> LoadAsync(string hash)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var now = DateTimeOffset.UtcNow;

        // No tenant context on this scope: the hash is the lookup, org comes from the row.
        return await db.ApiKeys.IgnoreQueryFilters()
            .Where(k => k.KeyHash == hash && k.RevokedAt == null && (k.ExpiresAt == null || k.ExpiresAt > now))
            .Join(db.Users.IgnoreQueryFilters().Where(u => u.IsActive), k => k.UserId, u => u.Id, (k, u) => new { k, u })
            .Join(db.Memberships.IgnoreQueryFilters(), x => new { x.k.UserId, x.k.WorkspaceId }, m => new { m.UserId, m.WorkspaceId },
                (x, m) => new KeyIdentity(x.k.Id, x.u.Id, x.k.OrganizationId, x.k.WorkspaceId, m.Role, x.u.Email))
            .FirstOrDefaultAsync();
    }

    /// <summary>Write last_used_at at most once a minute per key; a lost write is harmless.</summary>
    private async Task TouchLastUsedAsync(Guid keyId)
    {
        var flag = $"apikey-touch:{keyId}";
        if (_cache.TryGetValue(flag, out _)) return;
        _cache.Set(flag, true, LastUsedWriteInterval);
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            await db.ApiKeys.IgnoreQueryFilters().Where(k => k.Id == keyId)
                .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "last_used_at update skipped for key {KeyId}", keyId);
        }
    }

    private sealed record KeyIdentity(Guid KeyId, Guid UserId, Guid OrganizationId, Guid WorkspaceId, Role Role, string Email);
}
