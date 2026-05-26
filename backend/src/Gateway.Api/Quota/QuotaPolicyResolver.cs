using System.Text.Json;
using Gateway.Api.Domain;
using Gateway.Api.Persistence;
using Gateway.Api.Quota;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Gateway.Api.Gateway;

public sealed record EffectivePolicy(
    Guid OrganizationId,
    IReadOnlyList<string> AllowedModels,
    IReadOnlyList<QuotaWindow> UserWindows,
    IReadOnlyList<QuotaWindow> WorkspaceWindows,
    int RequestsPerMinute);

public interface IQuotaPolicyResolver
{
    Task<EffectivePolicy> ResolveAsync(QuotaPrincipal principal, CancellationToken ct);
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

    public QuotaPolicyResolver(GatewayDbContext db, IMemoryCache cache)
    {
        _db = db; _cache = cache;
    }

    public Task<EffectivePolicy> ResolveAsync(QuotaPrincipal p, CancellationToken ct) =>
        _cache.GetOrCreateAsync($"policy:{p.UserId}:{p.WorkspaceId}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);

            var ws = await _db.Workspaces.AsNoTracking()
                .FirstOrDefaultAsync(w => w.Id == p.WorkspaceId, ct)
                ?? throw new InvalidOperationException("Workspace not found.");

            var membership = await _db.Memberships.AsNoTracking()
                .FirstOrDefaultAsync(m => m.UserId == p.UserId && m.WorkspaceId == p.WorkspaceId, ct)
                ?? throw new UnauthorizedAccessException("User is not a member of this workspace.");

            var wsPolicy = Deserialize(ws.QuotaPolicyJson) ?? DefaultPolicy();
            var userWindows = membership.PerUserQuotaJson is not null
                ? Deserialize(membership.PerUserQuotaJson)?.UserWindows ?? wsPolicy.UserWindows
                : wsPolicy.UserWindows;

            return new EffectivePolicy(
                ws.OrganizationId,
                wsPolicy.AllowedModels,
                userWindows,
                wsPolicy.WorkspaceWindows,
                wsPolicy.RequestsPerMinute);
        })!;

    private static StoredPolicy? Deserialize(string? json) =>
        json is null ? null : JsonSerializer.Deserialize<StoredPolicy>(json);

    private static StoredPolicy DefaultPolicy() => new(
        AllowedModels: new[] { "claude-sonnet-4-6", "claude-opus-4-7" },
        // Anthropic-style: 100k / 5h per user, 1M / day at the workspace.
        UserWindows: new[] { QuotaWindow.Every(300, 100_000, "w5h") },
        WorkspaceWindows: new[] { QuotaWindow.Daily(1_000_000) },
        RequestsPerMinute: 20);

    private sealed record StoredPolicy(
        string[] AllowedModels,
        QuotaWindow[] UserWindows,
        QuotaWindow[] WorkspaceWindows,
        int RequestsPerMinute)
    {
        public static implicit operator EffectivePolicy(StoredPolicy s) =>
            new(Guid.Empty, s.AllowedModels, s.UserWindows, s.WorkspaceWindows, s.RequestsPerMinute);
    }
}
