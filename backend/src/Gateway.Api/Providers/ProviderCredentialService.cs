using System.Collections.Concurrent;
using System.Net;
using Gateway.Api.Domain;
using Gateway.Api.Persistence;
using Gateway.Api.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;

namespace Gateway.Api.Providers;

public enum AuthScheme { ApiKey, Bearer }

/// <summary>What a provider call should authenticate with. Never logged, never serialized.</summary>
public sealed record ProviderAuth(AuthScheme Scheme, string Secret, Guid CredentialId);

public sealed record ProviderCredentialRow(
    Guid Id, Guid? OrganizationId, string Provider, string Kind, string Hint, bool IsActive,
    DateTimeOffset? AccessExpiresAt, DateTimeOffset? LastRefreshedAt, string? LastError, DateTimeOffset CreatedAt);

public interface IProviderCredentialService
{
    /// <summary>
    /// The credential to use for this organization + provider: the org's own active
    /// credential, else the platform-wide one. OAuth tokens are refreshed here when
    /// close to expiry. Null when nothing usable is stored.
    /// </summary>
    Task<ProviderAuth?> ResolveAsync(Guid organizationId, string provider, CancellationToken ct = default);

    Task<Guid> AddApiKeyAsync(Guid? organizationId, string provider, string apiKey, Guid? actorId, CancellationToken ct = default);
    Task<Guid> AddOAuthAsync(Guid? organizationId, string provider, OAuthTokenSet tokens, string hint, Guid? actorId, CancellationToken ct = default);

    /// <summary>Soft-deletes. Scoped: an org admin can only touch rows owned by their org.</summary>
    Task<bool> DeactivateAsync(Guid? organizationId, Guid id, CancellationToken ct = default);

    /// <summary>organizationId null lists platform-wide rows; otherwise that org's rows.</summary>
    Task<IReadOnlyList<ProviderCredentialRow>> ListAsync(Guid? organizationId, CancellationToken ct = default);

    /// <summary>Cheap authenticated call (GET /v1/models) to confirm the credential works.</summary>
    Task<(bool ok, string message)> ProbeAsync(Guid? organizationId, Guid id, CancellationToken ct = default);

    /// <summary>Drop cached auth after an upstream 401 so the next call re-reads (and re-refreshes).</summary>
    void Invalidate(Guid organizationId, string provider);
}

/// <summary>
/// Singleton. Secrets live encrypted in Postgres; the decrypted auth for an
/// (org, provider) pair is cached in memory for at most 60s. OAuth refreshes are
/// serialized per credential across pods with a short Redis lock, and a refresh that
/// loses the race re-reads the row instead of declaring the credential dead.
/// </summary>
public sealed class ProviderCredentialService : IProviderCredentialService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RefreshLockTtl = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMemoryCache _cache;
    private readonly ISecretProtector _protector;
    private readonly OAuthTokenClient _oauth;
    private readonly AnthropicOAuthOptions _oauthOpt;
    private readonly AnthropicOptions _anthropic;
    private readonly IConnectionMultiplexer _redis;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ProviderCredentialService> _log;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _refreshGates = new();

    public ProviderCredentialService(
        IServiceScopeFactory scopeFactory, IMemoryCache cache, ISecretProtector protector,
        OAuthTokenClient oauth, AnthropicOAuthOptions oauthOpt, Microsoft.Extensions.Options.IOptions<AnthropicOptions> anthropic,
        IConnectionMultiplexer redis, IHttpClientFactory httpFactory, ILogger<ProviderCredentialService> log)
    {
        _scopeFactory = scopeFactory; _cache = cache; _protector = protector; _oauth = oauth;
        _oauthOpt = oauthOpt; _anthropic = anthropic.Value; _redis = redis; _httpFactory = httpFactory; _log = log;
    }

    private static string CacheKey(Guid org, string provider) => $"cred:{org}:{provider}";

    // ------------------------------------------------------------------ resolve

    public async Task<ProviderAuth?> ResolveAsync(Guid organizationId, string provider, CancellationToken ct = default)
    {
        var key = CacheKey(organizationId, provider);
        if (_cache.TryGetValue(key, out ProviderAuth? cached) && cached is not null) return cached;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

        // Org-owned first, platform-wide fallback second. IgnoreQueryFilters because this
        // singleton has no request tenant context; the WHERE clause is the scope.
        var row = await db.ProviderCredentials.IgnoreQueryFilters()
            .Where(c => c.Provider == provider && c.IsActive &&
                        (c.OrganizationId == organizationId || c.OrganizationId == null))
            .OrderByDescending(c => c.OrganizationId != null)
            .ThenByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (row is null) return null;

        ProviderAuth? auth;
        TimeSpan ttl = CacheTtl;

        if (row.Kind == CredentialKind.ApiKey)
        {
            auth = new ProviderAuth(AuthScheme.ApiKey, _protector.Unprotect(row.EncryptedSecret), row.Id);
        }
        else
        {
            if (NeedsRefresh(row))
                row = await RefreshAsync(db, row, ct);
            if (row is null) return null;

            auth = new ProviderAuth(AuthScheme.Bearer, _protector.Unprotect(row.EncryptedSecret), row.Id);
            if (row.AccessExpiresAt is { } exp)
            {
                var untilRefresh = exp - DateTimeOffset.UtcNow - TimeSpan.FromSeconds(_oauthOpt.RefreshSkewSeconds);
                if (untilRefresh < ttl) ttl = untilRefresh > TimeSpan.FromSeconds(5) ? untilRefresh : TimeSpan.FromSeconds(5);
            }
        }

        _cache.Set(key, auth, ttl);
        return auth;
    }

    private bool NeedsRefresh(ProviderCredential row) =>
        row.AccessExpiresAt is null ||
        row.AccessExpiresAt.Value - DateTimeOffset.UtcNow <= TimeSpan.FromSeconds(_oauthOpt.RefreshSkewSeconds);

    /// <summary>
    /// Refresh under a per-credential gate (in-process) and Redis lock (cross-pod).
    /// Returns the row holding a usable access token, or null if the grant is dead.
    /// </summary>
    private async Task<ProviderCredential?> RefreshAsync(GatewayDbContext db, ProviderCredential row, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(row.EncryptedRefreshToken))
        {
            await MarkDeadAsync(db, row, "no refresh token stored", ct);
            return null;
        }
        if (!_oauthOpt.Enabled)
        {
            _log.LogError("Credential {Id} needs an OAuth refresh but Anthropic:OAuth is not configured.", row.Id);
            return null;
        }

        var gate = _refreshGates.GetOrAdd(row.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var redis = _redis.GetDatabase();
            var lockKey = $"lock:cred-refresh:{row.Id}";
            var lockToken = Guid.NewGuid().ToString("N");
            var gotLock = await redis.StringSetAsync(lockKey, lockToken, RefreshLockTtl, When.NotExists);

            if (!gotLock)
            {
                // Another pod is refreshing. Wait for its write to land, then re-read.
                for (var i = 0; i < 25; i++)
                {
                    await Task.Delay(200, ct);
                    await db.Entry(row).ReloadAsync(ct);
                    if (!row.IsActive) return null;
                    if (!NeedsRefresh(row)) return row;
                }
                _log.LogWarning("Timed out waiting for another instance to refresh credential {Id}.", row.Id);
                return null;
            }

            try
            {
                // Re-read inside the lock: the in-process gate may have queued us behind a
                // refresh that already succeeded.
                await db.Entry(row).ReloadAsync(ct);
                var current = row;
                if (!current.IsActive) return null;
                if (!NeedsRefresh(current)) return current;

                var refreshToken = _protector.Unprotect(current.EncryptedRefreshToken!);
                OAuthTokenSet tokens;
                try
                {
                    tokens = await _oauth.RefreshAsync(refreshToken, ct);
                }
                catch (OAuthGrantRejectedException ex)
                {
                    await MarkDeadAsync(db, current, ex.Message, ct);
                    return null;
                }
                catch (HttpRequestException ex)
                {
                    // Transient. Keep serving the current token if it is still valid.
                    current.LastError = $"refresh failed: {ex.Message}";
                    current.UpdatedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct);
                    _log.LogWarning(ex, "Transient OAuth refresh failure for credential {Id}", current.Id);
                    return current.AccessExpiresAt > DateTimeOffset.UtcNow ? current : null;
                }

                current.EncryptedSecret = _protector.Protect(tokens.AccessToken);
                if (!string.IsNullOrEmpty(tokens.RefreshToken))
                    current.EncryptedRefreshToken = _protector.Protect(tokens.RefreshToken);
                current.AccessExpiresAt = tokens.ExpiresAt ?? DateTimeOffset.UtcNow.AddMinutes(30);
                current.Scopes = tokens.Scope ?? current.Scopes;
                current.LastRefreshedAt = DateTimeOffset.UtcNow;
                current.UpdatedAt = DateTimeOffset.UtcNow;
                current.LastError = null;
                await db.SaveChangesAsync(ct);
                _log.LogInformation("Refreshed OAuth credential {Id} (expires {Exp})", current.Id, current.AccessExpiresAt);
                return current;
            }
            finally
            {
                // Release only our own lock (compare-and-delete).
                await redis.ScriptEvaluateAsync(
                    "if redis.call('GET', KEYS[1]) == ARGV[1] then return redis.call('DEL', KEYS[1]) end return 0",
                    new RedisKey[] { lockKey }, new RedisValue[] { lockToken });
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task MarkDeadAsync(GatewayDbContext db, ProviderCredential row, string reason, CancellationToken ct)
    {
        row.IsActive = false;
        row.LastError = reason;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        InvalidateFor(row);
        _log.LogError("Provider credential {Id} ({Provider}) deactivated: {Reason}. An admin must reconnect it.",
            row.Id, row.Provider, reason);
    }

    // ------------------------------------------------------------------- writes

    public async Task<Guid> AddApiKeyAsync(Guid? organizationId, string provider, string apiKey, Guid? actorId, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

        var row = new ProviderCredential
        {
            OrganizationId = organizationId,
            Provider = provider,
            Kind = CredentialKind.ApiKey,
            EncryptedSecret = _protector.Protect(apiKey),
            Hint = SecretProtector.Hint(apiKey),
            CreatedBy = actorId,
        };
        db.ProviderCredentials.Add(row);
        await db.SaveChangesAsync(ct);
        InvalidateFor(row);
        return row.Id;
    }

    public async Task<Guid> AddOAuthAsync(Guid? organizationId, string provider, OAuthTokenSet tokens, string hint, Guid? actorId, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

        var row = new ProviderCredential
        {
            OrganizationId = organizationId,
            Provider = provider,
            Kind = CredentialKind.OAuth,
            EncryptedSecret = _protector.Protect(tokens.AccessToken),
            EncryptedRefreshToken = tokens.RefreshToken is null ? null : _protector.Protect(tokens.RefreshToken),
            AccessExpiresAt = tokens.ExpiresAt ?? DateTimeOffset.UtcNow.AddMinutes(30),
            Scopes = tokens.Scope,
            Hint = hint,
            CreatedBy = actorId,
            LastRefreshedAt = DateTimeOffset.UtcNow,
        };
        db.ProviderCredentials.Add(row);
        await db.SaveChangesAsync(ct);
        InvalidateFor(row);
        return row.Id;
    }

    public async Task<bool> DeactivateAsync(Guid? organizationId, Guid id, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

        var row = await db.ProviderCredentials.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == id && c.OrganizationId == organizationId, ct);
        if (row is null) return false;

        row.IsActive = false;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        InvalidateFor(row);
        return true;
    }

    public async Task<IReadOnlyList<ProviderCredentialRow>> ListAsync(Guid? organizationId, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

        return await db.ProviderCredentials.IgnoreQueryFilters()
            .Where(c => c.OrganizationId == organizationId)
            .OrderByDescending(c => c.CreatedAt)
            .Take(100)
            .Select(c => new ProviderCredentialRow(
                c.Id, c.OrganizationId, c.Provider, c.Kind == CredentialKind.OAuth ? "oauth" : "api_key",
                c.Hint, c.IsActive, c.AccessExpiresAt, c.LastRefreshedAt, c.LastError, c.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task<(bool ok, string message)> ProbeAsync(Guid? organizationId, Guid id, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

        var row = await db.ProviderCredentials.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == id && c.OrganizationId == organizationId && c.IsActive, ct);
        if (row is null) return (false, "Credential not found.");
        if (row.Provider != "anthropic") return (false, "Probe is only implemented for anthropic.");

        if (row.Kind == CredentialKind.OAuth && NeedsRefresh(row))
        {
            row = await RefreshAsync(db, row, ct);
            if (row is null) return (false, "OAuth token could not be refreshed; reconnect the account.");
        }

        var secret = _protector.Unprotect(row.EncryptedSecret);
        var http = _httpFactory.CreateClient("provider-probe");
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_anthropic.BaseUrl}/v1/models?limit=1");
        req.Headers.Add("anthropic-version", _anthropic.Version);
        ApplyAuth(req, row.Kind == CredentialKind.OAuth ? AuthScheme.Bearer : AuthScheme.ApiKey, secret);

        try
        {
            using var resp = await http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode) return (true, "Credential is valid.");
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return (false, $"Provider rejected the credential ({(int)resp.StatusCode}).");
            return (false, $"Provider returned {(int)resp.StatusCode}.");
        }
        catch (HttpRequestException ex)
        {
            return (false, $"Could not reach the provider: {ex.Message}");
        }
    }

    public void Invalidate(Guid organizationId, string provider) => _cache.Remove(CacheKey(organizationId, provider));

    private void InvalidateFor(ProviderCredential row)
    {
        if (row.OrganizationId is { } org)
        {
            _cache.Remove(CacheKey(org, row.Provider));
            return;
        }
        // Platform-wide rows are a fallback for every org; entries expire within 60s.
        // Evicting per-org here would need the org list, so rely on the short TTL.
    }

    /// <summary>
    /// API keys go on x-api-key. OAuth access tokens go on Authorization: Bearer and
    /// require the oauth-2025-04-20 beta header on /v1/messages.
    /// </summary>
    public static void ApplyAuth(HttpRequestMessage req, AuthScheme scheme, string secret)
    {
        if (scheme == AuthScheme.ApiKey)
        {
            req.Headers.Add("x-api-key", secret);
            return;
        }
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secret);
        req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
    }
}
