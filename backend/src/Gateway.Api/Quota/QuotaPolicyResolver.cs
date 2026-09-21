using System.Collections.Concurrent;
using System.Text.Json;
using Gateway.Api.Persistence;
using Gateway.Api.Quota;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;

namespace Gateway.Api.Gateway;

public sealed record EffectivePolicy(
    Guid OrganizationId,
    IReadOnlyList<string> AllowedModels,
    IReadOnlyList<QuotaWindow> UserWindows,
    IReadOnlyList<QuotaWindow> WorkspaceWindows,
    int RequestsPerMinute);

/// <summary>
/// The shape persisted in Workspace.QuotaPolicyJson. Also accepted (and partially
/// used) for Membership.PerUserQuotaJson, where only UserWindows + AllowedModels
/// are meaningful.
/// </summary>
public sealed record StoredPolicy(
    string[] AllowedModels,
    QuotaWindow[] UserWindows,
    QuotaWindow[] WorkspaceWindows,
    int RequestsPerMinute);

/// <summary>
/// The shape persisted in Membership.PerUserQuotaJson. Every field is optional —
/// a null field means "inherit the workspace value". A full StoredPolicy document
/// also deserializes into this, so older hand-written overrides keep working.
/// </summary>
public sealed record UserOverride(
    QuotaWindow[]? UserWindows,
    string[]? AllowedModels);

public interface IQuotaPolicyResolver
{
    Task<EffectivePolicy> ResolveAsync(QuotaPrincipal principal, CancellationToken ct);
}

/// <summary>
/// Caches resolved policies in-process and lets admin writes evict them early.
///
/// NOTE: this cache is per-pod. Evicting here makes the change visible immediately
/// on the pod that served the write; other replicas pick it up when their entry
/// expires (30s). That bound is the propagation guarantee, not the eviction.
/// </summary>
public interface IPolicyCache
{
    string KeyFor(Guid userId, Guid workspaceId);
    IChangeToken WorkspaceToken(Guid workspaceId);
    void InvalidateUser(Guid userId, Guid workspaceId);
    void InvalidateWorkspace(Guid workspaceId);
}

public sealed class PolicyCache : IPolicyCache
{
    private readonly IMemoryCache _cache;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _workspaceTokens = new();

    public PolicyCache(IMemoryCache cache) => _cache = cache;

    public string KeyFor(Guid userId, Guid workspaceId) => $"policy:{userId}:{workspaceId}";

    // Entries registered against this token are dropped when the workspace policy changes.
    public IChangeToken WorkspaceToken(Guid workspaceId) =>
        new CancellationChangeToken(_workspaceTokens.GetOrAdd(workspaceId, _ => new CancellationTokenSource()).Token);

    public void InvalidateUser(Guid userId, Guid workspaceId) => _cache.Remove(KeyFor(userId, workspaceId));

    public void InvalidateWorkspace(Guid workspaceId)
    {
        // Cancel without disposing: a concurrent WorkspaceToken call may still be
        // reading .Token, and Token throws once the source is disposed. The source
        // is dropped from the dictionary and collected normally.
        if (_workspaceTokens.TryRemove(workspaceId, out var cts))
            cts.Cancel();
    }
}

/// <summary>
/// Resolves the effective fair-usage policy by merging plan defaults, org settings,
/// workspace overrides, and per-user overrides. Cached briefly (30s) so the hot path
/// doesn't hit Postgres on every request, but admin changes still propagate fast.
/// </summary>
public sealed class QuotaPolicyResolver : IQuotaPolicyResolver
{
    private readonly GatewayDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly IPolicyCache _policyCache;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public QuotaPolicyResolver(GatewayDbContext db, IMemoryCache cache, IPolicyCache policyCache)
    {
        _db = db; _cache = cache; _policyCache = policyCache;
    }

    public Task<EffectivePolicy> ResolveAsync(QuotaPrincipal p, CancellationToken ct) =>
        _cache.GetOrCreateAsync(_policyCache.KeyFor(p.UserId, p.WorkspaceId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
            entry.AddExpirationToken(_policyCache.WorkspaceToken(p.WorkspaceId));

            var ws = await _db.Workspaces.AsNoTracking()
                .FirstOrDefaultAsync(w => w.Id == p.WorkspaceId, ct)
                ?? throw new InvalidOperationException("Workspace not found.");

            var membership = await _db.Memberships.AsNoTracking()
                .FirstOrDefaultAsync(m => m.UserId == p.UserId && m.WorkspaceId == p.WorkspaceId, ct)
                ?? throw new UnauthorizedAccessException("User is not a member of this workspace.");

            var wsPolicy = DeserializePolicy(ws.QuotaPolicyJson) ?? DefaultPolicy();
            var userOverride = DeserializeOverride(membership.PerUserQuotaJson);

            // A per-user override supplies windows and/or models; anything it omits
            // (or leaves empty) falls back to the workspace policy.
            var userWindows = userOverride?.UserWindows is { Length: > 0 } uw ? uw : wsPolicy.UserWindows;
            var allowedModels = userOverride?.AllowedModels is { Length: > 0 } am ? am : wsPolicy.AllowedModels;

            return new EffectivePolicy(
                ws.OrganizationId,
                allowedModels,
                userWindows,
                wsPolicy.WorkspaceWindows,
                wsPolicy.RequestsPerMinute);
        })!;

    public static StoredPolicy? DeserializePolicy(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<StoredPolicy>(json, JsonOptions);

    public static UserOverride? DeserializeOverride(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<UserOverride>(json, JsonOptions);

    public static StoredPolicy DefaultPolicy() => new(
        AllowedModels: new[] { "claude-sonnet-4-6", "claude-opus-4-7" },
        // Anthropic-style: 100k / 5h per user, 1M / day at the workspace.
        UserWindows: new[] { QuotaWindow.Every(300, 100_000, "w5h") },
        WorkspaceWindows: new[] { QuotaWindow.Daily(1_000_000) },
        RequestsPerMinute: 20);
}
