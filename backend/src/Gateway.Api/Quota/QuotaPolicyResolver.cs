using System.Collections.Concurrent;
using System.Text.Json;
using Gateway.Api.Billing;
using Gateway.Api.Domain;
using Gateway.Api.Persistence;
using Gateway.Api.Providers;
using Gateway.Api.Quota;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;

namespace Gateway.Api.Gateway;

/// <summary>
/// Everything the gateway needs to decide one request, resolved once and cached ~30s.
///
/// The first five members are the original shape and keep their positions so existing
/// callers are unaffected; the rest were added with the multi-provider allowance model
/// and default to "unrestricted" so a partially-configured tenant still works.
/// </summary>
public sealed record EffectivePolicy(
    Guid OrganizationId,
    IReadOnlyList<string> AllowedModels,
    IReadOnlyList<QuotaWindow> UserWindows,
    IReadOnlyList<QuotaWindow> WorkspaceWindows,
    int RequestsPerMinute,

    Guid MembershipId = default,
    Guid UserId = default,
    Guid WorkspaceId = default,
    Role Role = Role.User,
    /// <summary>Provider ids the member may reach. Empty = every provider the tenant allows.</summary>
    IReadOnlyList<string>? AllowedProviders = null,
    AiAccessStatus AiStatus = AiAccessStatus.Active,
    DateTimeOffset? AccessExpiresAt = null,
    bool OrganizationAiEnabled = true,
    bool OrganizationActive = true,
    string Currency = "USD",
    decimal UsdRate = 1m,
    int MarkupBps = 0,
    long? UserMonthlyAllowanceMinor = null,
    long? UserDailyAllowanceMinor = null,
    bool UserUnlimitedAllowance = false,
    long? OrganizationMonthlyBudgetMinor = null,
    int? RpmLimit = null,
    int? TpmLimit = null,
    int? ConcurrencyLimit = null,
    int? DailyRequestLimit = null)
{
    public OrganizationBilling Billing => new(Currency, UsdRate, MarkupBps);

    public bool AllowsModel(string model) =>
        AllowedModels.Count == 0 || AllowedModels.Contains(model, StringComparer.OrdinalIgnoreCase);

    public bool AllowsProvider(string provider) =>
        AllowedProviders is null || AllowedProviders.Count == 0 ||
        AllowedProviders.Contains(provider, StringComparer.OrdinalIgnoreCase);

    public bool AccessExpired => AccessExpiresAt is { } exp && exp <= DateTimeOffset.UtcNow;
}

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

/// <summary>
/// The shape persisted in Organization.AiPolicyJson: the tenant-wide ceiling every
/// workspace and member is narrowed within.
/// </summary>
public sealed record TenantAiPolicy(
    string[]? AllowedProviders,
    string[]? AllowedModels);

/// <summary>
/// Platform-wide restrictions from configuration (`Ai:Platform:*`). The outermost
/// layer: nothing below can widen it.
/// </summary>
public sealed class PlatformAiPolicyOptions
{
    /// <summary>Empty = every provider in the catalog.</summary>
    public List<string> AllowedProviders { get; set; } = new();
    /// <summary>Empty = every model a tenant allows.</summary>
    public List<string> AllowedModels { get; set; } = new();
    /// <summary>Requests per minute across the whole deployment. 0 = unlimited.</summary>
    public int PlatformRpm { get; set; }
    /// <summary>Default per-member requests per minute when nothing else sets one.</summary>
    public int DefaultUserRpm { get; set; } = 20;
}

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
    /// <summary>Drops every cached policy in an organization after a tenant-level change.</summary>
    void InvalidateOrganization(Guid organizationId);
    IChangeToken OrganizationToken(Guid organizationId);
}

public sealed class PolicyCache : IPolicyCache
{
    private readonly IMemoryCache _cache;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _workspaceTokens = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _organizationTokens = new();

    public PolicyCache(IMemoryCache cache) => _cache = cache;

    public string KeyFor(Guid userId, Guid workspaceId) => $"policy:{userId}:{workspaceId}";

    // Entries registered against this token are dropped when the workspace policy changes.
    public IChangeToken WorkspaceToken(Guid workspaceId) => TokenFor(_workspaceTokens, workspaceId);

    public IChangeToken OrganizationToken(Guid organizationId) => TokenFor(_organizationTokens, organizationId);

    public void InvalidateUser(Guid userId, Guid workspaceId) => _cache.Remove(KeyFor(userId, workspaceId));

    public void InvalidateWorkspace(Guid workspaceId) => Cancel(_workspaceTokens, workspaceId);

    public void InvalidateOrganization(Guid organizationId) => Cancel(_organizationTokens, organizationId);

    private static IChangeToken TokenFor(ConcurrentDictionary<Guid, CancellationTokenSource> map, Guid id) =>
        new CancellationChangeToken(map.GetOrAdd(id, _ => new CancellationTokenSource()).Token);

    private static void Cancel(ConcurrentDictionary<Guid, CancellationTokenSource> map, Guid id)
    {
        // Cancel without disposing: a concurrent token read may still be reading .Token,
        // which throws once the source is disposed. The source is dropped from the
        // dictionary and collected normally.
        if (map.TryRemove(id, out var cts)) cts.Cancel();
    }
}

/// <summary>
/// Resolves the effective policy by narrowing through every layer:
/// platform → tenant → workspace → role → user. Each layer can only restrict what the
/// layer above allowed, so the most restrictive applicable rule always wins and no
/// tenant setting can widen a platform one.
/// </summary>
public sealed class QuotaPolicyResolver : IQuotaPolicyResolver
{
    private readonly GatewayDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly IPolicyCache _policyCache;
    private readonly PlatformAiPolicyOptions _platform;
    private readonly BillingOptions _billing;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public QuotaPolicyResolver(
        GatewayDbContext db, IMemoryCache cache, IPolicyCache policyCache,
        PlatformAiPolicyOptions platform, BillingOptions billing)
    {
        _db = db; _cache = cache; _policyCache = policyCache; _platform = platform; _billing = billing;
    }

    public Task<EffectivePolicy> ResolveAsync(QuotaPrincipal p, CancellationToken ct) =>
        _cache.GetOrCreateAsync(_policyCache.KeyFor(p.UserId, p.WorkspaceId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
            entry.AddExpirationToken(_policyCache.WorkspaceToken(p.WorkspaceId));

            var ws = await _db.Workspaces.AsNoTracking()
                .FirstOrDefaultAsync(w => w.Id == p.WorkspaceId, ct)
                ?? throw new InvalidOperationException("Workspace not found.");

            entry.AddExpirationToken(_policyCache.OrganizationToken(ws.OrganizationId));

            var membership = await _db.Memberships.AsNoTracking()
                .FirstOrDefaultAsync(m => m.UserId == p.UserId && m.WorkspaceId == p.WorkspaceId, ct)
                ?? throw new UnauthorizedAccessException("User is not a member of this workspace.");

            // Organizations carry no tenant query filter (they are the tenant), so this
            // is scoped by the workspace we already resolved.
            var org = await _db.Organizations.AsNoTracking()
                .FirstOrDefaultAsync(o => o.Id == ws.OrganizationId, ct)
                ?? throw new InvalidOperationException("Organization not found.");

            var wsPolicy = DeserializePolicy(ws.QuotaPolicyJson) ?? DefaultPolicy();
            var userOverride = DeserializeOverride(membership.PerUserQuotaJson);
            var tenantPolicy = DeserializeTenantPolicy(org.AiPolicyJson);

            // Windows are not narrowed the same way: a per-user override replaces the
            // workspace windows outright, which is what an admin setting a member's own
            // limits means.
            var userWindows = userOverride?.UserWindows is { Length: > 0 } uw ? uw : wsPolicy.UserWindows;

            var allowedModels = Narrow(
                _platform.AllowedModels,
                tenantPolicy?.AllowedModels,
                wsPolicy.AllowedModels,
                RoleModelCeiling(membership.Role),
                userOverride?.AllowedModels);

            var allowedProviders = Narrow(
                _platform.AllowedProviders,
                tenantPolicy?.AllowedProviders,
                null,
                null,
                DeserializeStringArray(membership.AllowedProvidersJson));

            var currency = CurrencyInfo.Normalize(org.Currency);

            return new EffectivePolicy(
                ws.OrganizationId,
                allowedModels,
                userWindows,
                wsPolicy.WorkspaceWindows,
                wsPolicy.RequestsPerMinute,
                MembershipId: membership.Id,
                UserId: membership.UserId,
                WorkspaceId: membership.WorkspaceId,
                Role: membership.Role,
                AllowedProviders: allowedProviders,
                AiStatus: membership.AiStatus,
                AccessExpiresAt: membership.AccessExpiresAt,
                OrganizationAiEnabled: org.AiEnabled,
                OrganizationActive: org.IsActive && ws.IsActive,
                Currency: currency,
                UsdRate: org.UsdRate ?? _billing.RateFor(currency),
                MarkupBps: org.MarkupBps > 0 ? org.MarkupBps : _billing.DefaultMarkupBps,
                UserMonthlyAllowanceMinor: membership.MonthlyAllowanceMinor,
                UserDailyAllowanceMinor: membership.DailyAllowanceMinor,
                UserUnlimitedAllowance: membership.UnlimitedAllowance,
                OrganizationMonthlyBudgetMinor: org.MonthlyBudgetMinor,
                RpmLimit: membership.RpmLimit ?? (wsPolicy.RequestsPerMinute > 0 ? wsPolicy.RequestsPerMinute : _platform.DefaultUserRpm),
                TpmLimit: membership.TpmLimit,
                ConcurrencyLimit: membership.ConcurrencyLimit,
                DailyRequestLimit: membership.DailyRequestLimit);
        })!;

    /// <summary>
    /// Intersects the layers that express an opinion. A null or empty layer allows
    /// everything, an empty result after narrowing means the member may use nothing.
    /// </summary>
    internal static IReadOnlyList<string> Narrow(params IReadOnlyCollection<string>?[] layers)
    {
        List<string>? current = null;
        foreach (var layer in layers)
        {
            if (layer is null || layer.Count == 0) continue;
            if (current is null)
            {
                current = layer.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                continue;
            }
            current = current.Where(v => layer.Contains(v, StringComparer.OrdinalIgnoreCase)).ToList();
            if (current.Count == 0) break;
        }
        return current ?? (IReadOnlyList<string>)Array.Empty<string>();
    }

    /// <summary>
    /// Role-level model ceiling. Only a Viewer is capped by role today (no inference at
    /// all); every other role inherits whatever the tenant and workspace allow.
    /// </summary>
    private static IReadOnlyCollection<string>? RoleModelCeiling(Role role) =>
        role == Role.Viewer ? Array.Empty<string>() : null;

    public static StoredPolicy? DeserializePolicy(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<StoredPolicy>(json, JsonOptions);

    public static UserOverride? DeserializeOverride(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<UserOverride>(json, JsonOptions);

    public static TenantAiPolicy? DeserializeTenantPolicy(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<TenantAiPolicy>(json, JsonOptions);

    private static string[]? DeserializeStringArray(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<string[]>(json, JsonOptions);

    public static StoredPolicy DefaultPolicy() => new(
        AllowedModels: new[] { "claude-opus-5", "claude-sonnet-5", "claude-haiku-4-5" },
        // Anthropic-style: 100k / 5h per user, 1M / day at the workspace.
        UserWindows: new[] { QuotaWindow.Every(300, 100_000, "w5h") },
        WorkspaceWindows: new[] { QuotaWindow.Daily(1_000_000) },
        RequestsPerMinute: 20);
}
