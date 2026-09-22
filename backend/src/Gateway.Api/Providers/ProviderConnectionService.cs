using System.Collections.Concurrent;
using System.Text.Json;
using Gateway.Api.Domain;
// StackExchange.Redis also defines ConnectionType; the domain one is meant everywhere here.
using ConnectionType = Gateway.Api.Domain.ConnectionType;
using Gateway.Api.Persistence;
using Gateway.Api.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;

namespace Gateway.Api.Providers;

public enum AuthScheme { ApiKey, Bearer }

/// <summary>What a provider call should authenticate with. Never logged, never serialized.</summary>
public sealed record ProviderAuth(AuthScheme Scheme, string Secret, Guid CredentialId);

/// <summary>
/// A tenant's usable connection to a provider, with its secrets decrypted for the
/// duration of one call. Instances are cached in memory only, never serialized, and
/// never returned from a controller.
///
/// Secret carries the provider's primary credential: the API key, the OAuth access
/// token, the AWS secret access key, or the Google service-account JSON. Extra holds
/// the remaining secret parts (AWS access key id, session token); Config holds the
/// non-secret settings (region, project, location).
/// </summary>
public sealed record ResolvedConnection(
    Guid ConnectionId,
    Guid? OrganizationId,
    string Provider,
    ConnectionType Type,
    ConnectionStatus Status,
    string Secret,
    IReadOnlyDictionary<string, string> Extra,
    IReadOnlyDictionary<string, string> Config)
{
    public string? Config1(string key) => Config.TryGetValue(key, out var v) ? v : null;
    public string? Extra1(string key) => Extra.TryGetValue(key, out var v) ? v : null;
}

/// <summary>Safe projection of a connection for APIs and dashboards. Contains no secret material.</summary>
public sealed record ProviderConnectionView(
    Guid Id,
    Guid? OrganizationId,
    string Provider,
    string ProviderDisplayName,
    string ConnectionType,
    string Status,
    string ConnectionPurpose,
    string? DisplayName,
    string Hint,
    string? ProviderAccountId,
    string? ProviderOrganizationId,
    IReadOnlyDictionary<string, string> Config,
    string? Scopes,
    int EncryptionKeyVersion,
    Guid? ConnectedByUserId,
    DateTimeOffset ConnectedAt,
    DateTimeOffset? AccessExpiresAt,
    DateTimeOffset? LastValidatedAt,
    DateTimeOffset? LastRefreshedAt,
    DateTimeOffset? RevokedAt,
    int FailureCount,
    DateTimeOffset? LastFailureAt,
    string? LastFailureReason);

public sealed record ConnectRequest(
    Guid? OrganizationId,
    string Provider,
    ConnectionType Type,
    string Secret,
    IReadOnlyDictionary<string, string>? Extra,
    IReadOnlyDictionary<string, string>? Config,
    string? DisplayName,
    string? ProviderAccountId,
    string ConnectionPurpose = "default");

public sealed record ConnectResult(Guid Id, string Status, string Hint);

/// <summary>Raised when the caller asks for a connection operation that the stored state forbids.</summary>
public sealed class ConnectionStateException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public interface IProviderConnectionService
{
    /// <summary>
    /// The connection this organization should use for a provider: its own live
    /// connection, else the platform-wide fallback. OAuth tokens are refreshed here
    /// when close to expiry. Null when nothing usable is connected.
    /// </summary>
    Task<ResolvedConnection?> ResolveAsync(Guid organizationId, string provider, CancellationToken ct = default);

    /// <summary>Provider ids this organization has a servable connection for.</summary>
    Task<IReadOnlyList<string>> ConnectedProvidersAsync(Guid organizationId, CancellationToken ct = default);

    Task<IReadOnlyList<ProviderConnectionView>> ListAsync(Guid? organizationId, CancellationToken ct = default);
    Task<ProviderConnectionView?> GetAsync(Guid? organizationId, Guid id, CancellationToken ct = default);

    Task<ConnectResult> ConnectAsync(ConnectRequest request, Guid? actorId, CancellationToken ct = default);

    /// <summary>Stores the token set from a completed OAuth authorization-code flow.</summary>
    Task<ConnectResult> CompleteOAuthAsync(
        Guid? organizationId, string provider, OAuthTokenSet tokens, string hint, string? accountId,
        Guid? actorId, CancellationToken ct = default);

    /// <summary>Authenticated probe against the provider. Updates status and last-validated.</summary>
    Task<(bool ok, string message)> ValidateAsync(Guid? organizationId, Guid id, CancellationToken ct = default);

    /// <summary>Revokes upstream where supported, clears the secrets and marks the row Revoked.</summary>
    Task<bool> DisconnectAsync(Guid? organizationId, Guid id, Guid? actorId, CancellationToken ct = default);

    /// <summary>Disable pauses a connection without discarding it; enable puts it back in service.</summary>
    Task<bool> SetEnabledAsync(Guid? organizationId, Guid id, bool enabled, CancellationToken ct = default);

    /// <summary>Records an upstream rejection, moving the connection into the matching failed state.</summary>
    Task ReportFailureAsync(Guid connectionId, int? httpStatus, string reason, CancellationToken ct = default);

    /// <summary>Clears the failure counter after a successful call.</summary>
    Task ReportSuccessAsync(Guid connectionId, CancellationToken ct = default);

    /// <summary>Re-seals every stored secret under the current data key. Returns rows rotated.</summary>
    Task<int> RotateEncryptionAsync(CancellationToken ct = default);

    void Invalidate(Guid organizationId, string provider);
}

/// <summary>
/// Singleton owner of provider connections. Secrets live AES-256-GCM encrypted in
/// Postgres under a versioned data key; the decrypted form for an (org, provider)
/// pair is cached in memory for at most 60s and never crosses a process boundary.
///
/// OAuth refreshes are serialized per connection across pods with a short Redis lock,
/// and a refresh that loses the race re-reads the row rather than declaring the
/// connection dead.
/// </summary>
public sealed class ProviderConnectionService : IProviderConnectionService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RefreshLockTtl = TimeSpan.FromSeconds(30);
    private const int FailuresBeforeError = 3;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMemoryCache _cache;
    private readonly ICredentialEncryption _crypto;
    private readonly OAuthTokenClient _oauth;
    private readonly ProviderOAuthRegistry _oauthRegistry;
    private readonly IProviderValidator _validator;
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<ProviderConnectionService> _log;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _refreshGates = new();

    public ProviderConnectionService(
        IServiceScopeFactory scopeFactory, IMemoryCache cache, ICredentialEncryption crypto,
        OAuthTokenClient oauth, ProviderOAuthRegistry oauthRegistry, IProviderValidator validator,
        IConnectionMultiplexer redis, ILogger<ProviderConnectionService> log)
    {
        _scopeFactory = scopeFactory; _cache = cache; _crypto = crypto; _oauth = oauth;
        _oauthRegistry = oauthRegistry; _validator = validator; _redis = redis; _log = log;
    }

    private static string CacheKey(Guid org, string provider) => $"conn:{org}:{provider}";
    private static string ConnectedListKey(Guid org) => $"conn-list:{org}";

    // ------------------------------------------------------------------ resolve

    public async Task<ResolvedConnection?> ResolveAsync(Guid organizationId, string provider, CancellationToken ct = default)
    {
        provider = ProviderCatalog.Normalize(provider) ?? provider;
        var key = CacheKey(organizationId, provider);
        if (_cache.TryGetValue(key, out ResolvedConnection? cached) && cached is not null) return cached;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

        // Org-owned first, platform-wide fallback second. IgnoreQueryFilters because this
        // singleton has no request tenant context; the WHERE clause is the scope.
        var candidates = await db.ProviderCredentials.IgnoreQueryFilters()
            .Where(c => c.Provider == provider &&
                        (c.OrganizationId == organizationId || c.OrganizationId == null))
            .OrderByDescending(c => c.OrganizationId != null)
            .ThenByDescending(c => c.CreatedAt)
            .Take(8)
            .ToListAsync(ct);

        var row = candidates.FirstOrDefault(c => ConnectionStatuses.CanServe(c.Status));
        if (row is null) return null;

        var resolved = await MaterializeAsync(db, row, ct);
        if (resolved is null) return null;

        var ttl = CacheTtl;
        if (row.ConnectionType == ConnectionType.OAuth && row.AccessExpiresAt is { } exp)
        {
            var untilRefresh = exp - DateTimeOffset.UtcNow - TimeSpan.FromSeconds(RefreshSkew(provider));
            if (untilRefresh < ttl) ttl = untilRefresh > TimeSpan.FromSeconds(5) ? untilRefresh : TimeSpan.FromSeconds(5);
        }
        _cache.Set(key, resolved, ttl);
        return resolved;
    }

    public async Task<IReadOnlyList<string>> ConnectedProvidersAsync(Guid organizationId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(ConnectedListKey(organizationId), out IReadOnlyList<string>? cached) && cached is not null)
            return cached;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

        var serveable = ConnectionStatuses.Live;
        var rows = await db.ProviderCredentials.IgnoreQueryFilters()
            .Where(c => (c.OrganizationId == organizationId || c.OrganizationId == null) && serveable.Contains(c.Status))
            .Select(c => c.Provider)
            .Distinct()
            .ToListAsync(ct);

        _cache.Set(ConnectedListKey(organizationId), (IReadOnlyList<string>)rows, TimeSpan.FromSeconds(30));
        return rows;
    }

    /// <summary>Decrypts a row into a usable connection, refreshing an OAuth token first when needed.</summary>
    private async Task<ResolvedConnection?> MaterializeAsync(GatewayDbContext db, ProviderCredential row, CancellationToken ct)
    {
        if (row.ConnectionType == ConnectionType.OAuth && NeedsRefresh(row))
        {
            var refreshed = await RefreshAsync(db, row, ct);
            if (refreshed is null) return null;
            row = refreshed;
        }

        string secret;
        Dictionary<string, string> extra;
        try
        {
            secret = _crypto.Decrypt(row.EncryptedSecret, row.EncryptionKeyVersion);
            extra = DecryptExtra(row);
        }
        catch (Exception ex)
        {
            // A key that is no longer loaded, or a tampered row. Never surface the detail.
            _log.LogError(ex, "Connection {Id} ({Provider}) could not be decrypted under key version {Version}.",
                row.Id, row.Provider, row.EncryptionKeyVersion);
            return null;
        }

        return new ResolvedConnection(
            row.Id, row.OrganizationId, row.Provider, row.ConnectionType, row.Status,
            secret, extra, ParseConfig(row.ConfigJson));
    }

    private Dictionary<string, string> DecryptExtra(ProviderCredential row)
    {
        if (string.IsNullOrEmpty(row.EncryptedConfig)) return new Dictionary<string, string>();
        var json = _crypto.Decrypt(row.EncryptedConfig, row.EncryptionKeyVersion);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
    }

    private static Dictionary<string, string> ParseConfig(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();

    private int RefreshSkew(string provider) => _oauthRegistry.For(provider)?.RefreshSkewSeconds ?? 120;

    private bool NeedsRefresh(ProviderCredential row) =>
        row.AccessExpiresAt is null ||
        row.AccessExpiresAt.Value - DateTimeOffset.UtcNow <= TimeSpan.FromSeconds(RefreshSkew(row.Provider));

    // ------------------------------------------------------------------ refresh

    /// <summary>
    /// Refresh under a per-connection gate (in-process) and Redis lock (cross-pod).
    /// Returns the row holding a usable access token, or null if the grant is dead.
    /// </summary>
    private async Task<ProviderCredential?> RefreshAsync(GatewayDbContext db, ProviderCredential row, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(row.EncryptedRefreshToken))
        {
            await MarkAsync(db, row, ConnectionStatus.ReauthenticationRequired, "no refresh token stored", ct);
            return null;
        }

        var oauthOpt = _oauthRegistry.For(row.Provider);
        if (oauthOpt is null)
        {
            _log.LogError("Connection {Id} needs an OAuth refresh but no OAuth client is configured for {Provider}.",
                row.Id, row.Provider);
            await MarkAsync(db, row, ConnectionStatus.Error, "OAuth client is not configured on this gateway", ct);
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
                    if (!ConnectionStatuses.CanServe(row.Status)) return null;
                    if (!NeedsRefresh(row)) return row;
                }
                _log.LogWarning("Timed out waiting for another instance to refresh connection {Id}.", row.Id);
                return null;
            }

            try
            {
                // Re-read inside the lock: the in-process gate may have queued us behind a
                // refresh that already succeeded.
                await db.Entry(row).ReloadAsync(ct);
                var current = row;
                if (!ConnectionStatuses.CanServe(current.Status)) return null;
                if (!NeedsRefresh(current)) return current;

                var refreshToken = _crypto.Decrypt(current.EncryptedRefreshToken!, current.EncryptionKeyVersion);
                OAuthTokenSet tokens;
                try
                {
                    tokens = await _oauth.RefreshAsync(oauthOpt, refreshToken, ct);
                }
                catch (OAuthGrantRejectedException ex)
                {
                    // The provider repudiated the grant: only a fresh admin login fixes it.
                    await MarkAsync(db, current, ConnectionStatus.ReauthenticationRequired, ex.Message, ct);
                    return null;
                }
                catch (HttpRequestException ex)
                {
                    // Transient. Keep serving the current token if it is still valid.
                    current.LastError = $"refresh failed: {ex.Message}";
                    current.LastFailureReason = current.LastError;
                    current.LastFailureAt = DateTimeOffset.UtcNow;
                    current.UpdatedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct);
                    _log.LogWarning(ex, "Transient OAuth refresh failure for connection {Id}", current.Id);
                    return current.AccessExpiresAt > DateTimeOffset.UtcNow ? current : null;
                }

                // Refresh-token rotation: the replacement is written in the same save as the
                // new access token, so a crash can never leave the two out of step.
                var sealedAccess = _crypto.Encrypt(tokens.AccessToken);
                current.EncryptedSecret = sealedAccess.Ciphertext;
                current.EncryptionKeyVersion = sealedAccess.KeyVersion;
                if (!string.IsNullOrEmpty(tokens.RefreshToken))
                    current.EncryptedRefreshToken = _crypto.Encrypt(tokens.RefreshToken).Ciphertext;
                if (!string.IsNullOrEmpty(current.EncryptedConfig))
                    current.EncryptedConfig = _crypto.Encrypt(_crypto.Decrypt(current.EncryptedConfig, current.EncryptionKeyVersion)).Ciphertext;
                current.AccessExpiresAt = tokens.ExpiresAt ?? DateTimeOffset.UtcNow.AddMinutes(30);
                current.Scopes = tokens.Scope ?? current.Scopes;
                current.LastRefreshedAt = DateTimeOffset.UtcNow;
                current.UpdatedAt = DateTimeOffset.UtcNow;
                current.Status = ConnectionStatus.Connected;
                current.FailureCount = 0;
                current.LastError = null;
                current.LastFailureReason = null;
                await db.SaveChangesAsync(ct);
                InvalidateFor(current);
                _log.LogInformation("Refreshed OAuth connection {Id} (expires {Exp})", current.Id, current.AccessExpiresAt);
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

    private async Task MarkAsync(GatewayDbContext db, ProviderCredential row, ConnectionStatus status, string reason, CancellationToken ct)
    {
        row.Status = status;
        row.IsActive = ConnectionStatuses.CanServe(status);
        row.LastError = reason;
        row.LastFailureReason = reason;
        row.LastFailureAt = DateTimeOffset.UtcNow;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        InvalidateFor(row);
        _log.LogError("Provider connection {Id} ({Provider}) moved to {Status}: {Reason}.",
            row.Id, row.Provider, ConnectionStatuses.Wire(status), reason);
    }

    // ------------------------------------------------------------------- writes

    public async Task<ConnectResult> ConnectAsync(ConnectRequest request, Guid? actorId, CancellationToken ct = default)
    {
        var provider = ProviderCatalog.Normalize(request.Provider)
            ?? throw new ConnectionStateException("unknown_provider", $"'{request.Provider}' is not a supported provider.");
        var descriptor = ProviderCatalog.Find(provider)!;

        if (!descriptor.Supports(request.Type))
            throw new ConnectionStateException("unsupported_connection_type",
                $"{descriptor.DisplayName} does not support that connection method.");

        var config = new Dictionary<string, string>(request.Config ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var required in descriptor.RequiredConfigKeys)
            if (!config.TryGetValue(required, out var v) || string.IsNullOrWhiteSpace(v))
                throw new ConnectionStateException("missing_config", $"'{required}' is required to connect {descriptor.DisplayName}.");

        // A tenant-supplied endpoint could otherwise aim the gateway's credentials at
        // an internal address; only hosts the descriptor allows are accepted.
        if (config.TryGetValue("baseUrl", out var baseUrl) && !string.IsNullOrWhiteSpace(baseUrl)
            && !ProviderCatalog.IsAllowedEndpoint(descriptor, baseUrl))
            throw new ConnectionStateException("endpoint_not_allowed",
                $"Base URL must be an https {descriptor.DisplayName} endpoint.");

        var purpose = string.IsNullOrWhiteSpace(request.ConnectionPurpose) ? "default" : request.ConnectionPurpose.Trim().ToLowerInvariant();

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // One live connection per (tenant, provider, purpose): supersede the old one in
        // the same transaction that inserts the replacement, so the unique index holds
        // and a reconnect is never a window with no connection at all.
        var live = ConnectionStatuses.Live;
        var existing = await db.ProviderCredentials.IgnoreQueryFilters()
            .Where(c => c.OrganizationId == request.OrganizationId && c.Provider == provider
                        && c.ConnectionPurpose == purpose && live.Contains(c.Status))
            .ToListAsync(ct);
        foreach (var old in existing)
        {
            old.Status = ConnectionStatus.Disabled;
            old.IsActive = false;
            old.UpdatedAt = DateTimeOffset.UtcNow;
            old.LastFailureReason = "replaced by a new connection";
        }

        var sealedSecret = _crypto.Encrypt(request.Secret);
        var row = new ProviderCredential
        {
            OrganizationId = request.OrganizationId,
            Provider = provider,
            ConnectionType = request.Type,
            Kind = request.Type == ConnectionType.OAuth ? CredentialKind.OAuth : CredentialKind.ApiKey,
            Status = ConnectionStatus.Connected,
            ConnectionPurpose = purpose,
            DisplayName = Truncate(request.DisplayName, 120),
            ProviderAccountId = Truncate(request.ProviderAccountId, 200),
            EncryptedSecret = sealedSecret.Ciphertext,
            EncryptionKeyVersion = sealedSecret.KeyVersion,
            EncryptedConfig = request.Extra is { Count: > 0 }
                ? _crypto.Encrypt(JsonSerializer.Serialize(request.Extra)).Ciphertext
                : null,
            ConfigJson = config.Count > 0 ? JsonSerializer.Serialize(config) : null,
            Hint = HintFor(request.Type, request.Secret, request.Extra, config),
            IsActive = true,
            CreatedBy = actorId,
            LastValidatedAt = null,
        };
        db.ProviderCredentials.Add(row);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        InvalidateFor(row);
        return new ConnectResult(row.Id, ConnectionStatuses.Wire(row.Status), row.Hint);
    }

    /// <summary>
    /// Stores the token set from a completed OAuth flow. Separate from ConnectAsync
    /// because the refresh token and expiry come from the provider, not the admin.
    /// </summary>
    public async Task<ConnectResult> CompleteOAuthAsync(
        Guid? organizationId, string provider, OAuthTokenSet tokens, string hint, string? accountId, Guid? actorId, CancellationToken ct = default)
    {
        var result = await ConnectAsync(new ConnectRequest(
            organizationId, provider, ConnectionType.OAuth, tokens.AccessToken,
            Extra: null, Config: null, DisplayName: hint, ProviderAccountId: accountId), actorId, ct);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var row = await db.ProviderCredentials.IgnoreQueryFilters().FirstAsync(c => c.Id == result.Id, ct);
        row.EncryptedRefreshToken = tokens.RefreshToken is null ? null : _crypto.Encrypt(tokens.RefreshToken).Ciphertext;
        row.AccessExpiresAt = tokens.ExpiresAt ?? DateTimeOffset.UtcNow.AddMinutes(30);
        row.Scopes = tokens.Scope;
        row.Hint = hint;
        row.LastRefreshedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        InvalidateFor(row);
        return result with { Hint = hint };
    }

    public async Task<IReadOnlyList<ProviderConnectionView>> ListAsync(Guid? organizationId, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

        var rows = await db.ProviderCredentials.IgnoreQueryFilters()
            .Where(c => c.OrganizationId == organizationId)
            .OrderByDescending(c => c.CreatedAt)
            .Take(200)
            .ToListAsync(ct);

        return rows.Select(ToView).ToList();
    }

    public async Task<ProviderConnectionView?> GetAsync(Guid? organizationId, Guid id, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var row = await db.ProviderCredentials.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == id && c.OrganizationId == organizationId, ct);
        return row is null ? null : ToView(row);
    }

    public async Task<(bool ok, string message)> ValidateAsync(Guid? organizationId, Guid id, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

        var row = await db.ProviderCredentials.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == id && c.OrganizationId == organizationId, ct);
        if (row is null) return (false, "Connection not found.");
        if (row.Status is ConnectionStatus.Revoked) return (false, "This connection has been disconnected.");

        var resolved = await MaterializeAsync(db, row, ct);
        if (resolved is null)
        {
            await db.Entry(row).ReloadAsync(ct);
            return (false, row.Status == ConnectionStatus.ReauthenticationRequired
                ? "The provider no longer accepts this login. Reconnect the account."
                : "The stored credential could not be used. Reconnect the account.");
        }

        var (ok, message) = await _validator.ProbeAsync(resolved, ct);

        row.LastValidatedAt = DateTimeOffset.UtcNow;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        if (ok)
        {
            row.Status = ConnectionStatus.Connected;
            row.IsActive = true;
            row.FailureCount = 0;
            row.LastError = null;
            row.LastFailureReason = null;
        }
        else
        {
            row.FailureCount++;
            row.LastFailureAt = DateTimeOffset.UtcNow;
            row.LastFailureReason = Truncate(message, 400);
            row.LastError = row.LastFailureReason;
            if (row.Status == ConnectionStatus.Connected) row.Status = ConnectionStatus.Error;
        }
        await db.SaveChangesAsync(ct);
        InvalidateFor(row);
        return (ok, message);
    }

    public async Task<bool> DisconnectAsync(Guid? organizationId, Guid id, Guid? actorId, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

        var row = await db.ProviderCredentials.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == id && c.OrganizationId == organizationId, ct);
        if (row is null) return false;
        if (row.Status == ConnectionStatus.Revoked) return true;

        // Best effort upstream revocation before the secret is destroyed locally.
        if (row.ConnectionType == ConnectionType.OAuth && _oauthRegistry.For(row.Provider) is { } opt &&
            !string.IsNullOrEmpty(opt.RevokeUrl))
        {
            try
            {
                var token = _crypto.Decrypt(row.EncryptedRefreshToken ?? row.EncryptedSecret, row.EncryptionKeyVersion);
                await _oauth.RevokeAsync(opt, token, ct);
            }
            catch (Exception ex)
            {
                // Local revocation still proceeds: the tenant asked to stop using it.
                _log.LogWarning(ex, "Upstream token revocation failed for connection {Id}; disconnecting locally anyway.", row.Id);
            }
        }

        // The secrets go, the row (and therefore the accounting history that references
        // it) stays.
        row.Status = ConnectionStatus.Revoked;
        row.IsActive = false;
        row.RevokedAt = DateTimeOffset.UtcNow;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        row.EncryptedSecret = "";
        row.EncryptedRefreshToken = null;
        row.EncryptedConfig = null;
        row.AccessExpiresAt = null;
        await db.SaveChangesAsync(ct);
        InvalidateFor(row);
        _log.LogInformation("Provider connection {Id} ({Provider}) disconnected by {Actor}.", row.Id, row.Provider, actorId);
        return true;
    }

    public async Task<bool> SetEnabledAsync(Guid? organizationId, Guid id, bool enabled, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

        var row = await db.ProviderCredentials.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == id && c.OrganizationId == organizationId, ct);
        if (row is null) return false;
        if (row.Status == ConnectionStatus.Revoked)
            throw new ConnectionStateException("connection_revoked", "A disconnected connection cannot be re-enabled; connect again.");

        if (enabled)
        {
            var live = ConnectionStatuses.Live;
            var conflict = await db.ProviderCredentials.IgnoreQueryFilters().AnyAsync(
                c => c.Id != row.Id && c.OrganizationId == row.OrganizationId && c.Provider == row.Provider
                     && c.ConnectionPurpose == row.ConnectionPurpose && live.Contains(c.Status), ct);
            if (conflict)
                throw new ConnectionStateException("connection_conflict",
                    "Another connection for this provider is already active. Disable it first.");
        }

        row.Status = enabled ? ConnectionStatus.Connected : ConnectionStatus.Disabled;
        row.IsActive = enabled;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        InvalidateFor(row);
        return true;
    }

    public async Task ReportFailureAsync(Guid connectionId, int? httpStatus, string reason, CancellationToken ct = default)
    {
        if (connectionId == Guid.Empty) return;
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

        var row = await db.ProviderCredentials.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == connectionId, ct);
        if (row is null) return;

        row.FailureCount++;
        row.LastFailureAt = DateTimeOffset.UtcNow;
        row.LastFailureReason = Truncate(reason, 400);
        row.LastError = row.LastFailureReason;
        row.UpdatedAt = DateTimeOffset.UtcNow;

        // 401/403 means the credential itself is bad: an API key needs replacing, an
        // OAuth grant needs the admin to log in again. Either way, stop serving it.
        if (httpStatus is 401 or 403)
        {
            row.Status = row.ConnectionType == ConnectionType.OAuth
                ? ConnectionStatus.ReauthenticationRequired
                : ConnectionStatus.Error;
            row.IsActive = false;
        }
        else if (row.FailureCount >= FailuresBeforeError && row.Status == ConnectionStatus.Connected)
        {
            row.Status = ConnectionStatus.Error;
        }

        await db.SaveChangesAsync(ct);
        InvalidateFor(row);
    }

    public async Task ReportSuccessAsync(Guid connectionId, CancellationToken ct = default)
    {
        if (connectionId == Guid.Empty) return;
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

        // Only touch rows that have something to clear, so the happy path is a no-op.
        await db.ProviderCredentials.IgnoreQueryFilters()
            .Where(c => c.Id == connectionId && (c.FailureCount > 0 || c.Status == ConnectionStatus.Error))
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.FailureCount, 0)
                .SetProperty(c => c.Status, ConnectionStatus.Connected)
                .SetProperty(c => c.LastFailureReason, (string?)null)
                .SetProperty(c => c.LastError, (string?)null), ct);
    }

    public async Task<int> RotateEncryptionAsync(CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

        var stale = await db.ProviderCredentials.IgnoreQueryFilters()
            .Where(c => c.EncryptionKeyVersion != _crypto.CurrentKeyVersion && c.Status != ConnectionStatus.Revoked)
            .Take(200)
            .ToListAsync(ct);

        var rotated = 0;
        foreach (var row in stale)
        {
            try
            {
                var version = row.EncryptionKeyVersion;
                if (!string.IsNullOrEmpty(row.EncryptedSecret))
                    row.EncryptedSecret = _crypto.Encrypt(_crypto.Decrypt(row.EncryptedSecret, version)).Ciphertext;
                if (!string.IsNullOrEmpty(row.EncryptedRefreshToken))
                    row.EncryptedRefreshToken = _crypto.Encrypt(_crypto.Decrypt(row.EncryptedRefreshToken, version)).Ciphertext;
                if (!string.IsNullOrEmpty(row.EncryptedConfig))
                    row.EncryptedConfig = _crypto.Encrypt(_crypto.Decrypt(row.EncryptedConfig, version)).Ciphertext;
                row.EncryptionKeyVersion = _crypto.CurrentKeyVersion;
                row.UpdatedAt = DateTimeOffset.UtcNow;
                rotated++;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Could not rotate encryption for connection {Id}; leaving it on version {Version}.",
                    row.Id, row.EncryptionKeyVersion);
            }
        }

        if (rotated > 0)
        {
            await db.SaveChangesAsync(ct);
            foreach (var row in stale) InvalidateFor(row);
            _log.LogInformation("Re-sealed {Count} provider connections under key version {Version}.",
                rotated, _crypto.CurrentKeyVersion);
        }
        return rotated;
    }

    public void Invalidate(Guid organizationId, string provider)
    {
        _cache.Remove(CacheKey(organizationId, ProviderCatalog.Normalize(provider) ?? provider));
        _cache.Remove(ConnectedListKey(organizationId));
    }

    private void InvalidateFor(ProviderCredential row)
    {
        if (row.OrganizationId is { } org)
        {
            Invalidate(org, row.Provider);
            return;
        }
        // Platform-wide rows are a fallback for every org; evicting per-org here would
        // need the org list, so rely on the 60s TTL.
    }

    // ------------------------------------------------------------------ helpers

    internal static ProviderConnectionView ToView(ProviderCredential c) => new(
        c.Id, c.OrganizationId, c.Provider,
        ProviderCatalog.Find(c.Provider)?.DisplayName ?? c.Provider,
        WireType(c.ConnectionType), ConnectionStatuses.Wire(c.Status), c.ConnectionPurpose, c.DisplayName,
        c.Hint, c.ProviderAccountId, c.ProviderOrganizationId,
        ParseConfig(c.ConfigJson), c.Scopes, c.EncryptionKeyVersion, c.CreatedBy, c.CreatedAt,
        c.AccessExpiresAt, c.LastValidatedAt, c.LastRefreshedAt, c.RevokedAt,
        c.FailureCount, c.LastFailureAt, c.LastFailureReason);

    public static string WireType(ConnectionType t) => t switch
    {
        ConnectionType.OAuth => "oauth",
        ConnectionType.AwsBedrock => "aws_bedrock",
        ConnectionType.GoogleVertex => "google_vertex",
        _ => "api_key",
    };

    public static ConnectionType ParseType(string? wire) => wire?.Trim().ToLowerInvariant() switch
    {
        "oauth" => ConnectionType.OAuth,
        "aws_bedrock" or "bedrock" => ConnectionType.AwsBedrock,
        "google_vertex" or "vertex" => ConnectionType.GoogleVertex,
        _ => ConnectionType.ApiKey,
    };

    /// <summary>
    /// A display label that identifies the credential without weakening it: the last
    /// four characters of an API key, the AWS access key id (an identifier, not a
    /// secret), or the service-account email.
    /// </summary>
    private static string HintFor(ConnectionType type, string secret,
        IReadOnlyDictionary<string, string>? extra, IReadOnlyDictionary<string, string> config)
    {
        switch (type)
        {
            case ConnectionType.AwsBedrock:
                var keyId = extra is not null && extra.TryGetValue("accessKeyId", out var k) ? k : "";
                var region = config.TryGetValue("region", out var r) ? r : "";
                return keyId.Length >= 8 ? $"{keyId[..4]}…{keyId[^4..]} · {region}" : region;
            case ConnectionType.GoogleVertex:
                var project = config.TryGetValue("project", out var p) ? p : "";
                var email = TryReadServiceAccountEmail(secret);
                return email is null ? project : $"{email} · {project}";
            default:
                return SecretProtector.Hint(secret);
        }
    }

    private static string? TryReadServiceAccountEmail(string serviceAccountJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(serviceAccountJson);
            return doc.RootElement.TryGetProperty("client_email", out var e) ? e.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static string? Truncate(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];

    /// <summary>
    /// API keys go on x-api-key. OAuth access tokens go on Authorization: Bearer and
    /// require the oauth-2025-04-20 beta header on Anthropic's /v1/messages.
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
