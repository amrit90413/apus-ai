using System.Security.Claims;
using Gateway.Api.Domain;
using Gateway.Api.Gateway;
using Gateway.Api.Persistence;
using Gateway.Api.Quota;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace Gateway.Api.Admin;

/// <summary>
/// Read models behind the dashboards: the super-admin cross-tenant rollup and
/// anomaly feed, and the org-admin "top consumers this window" view. Usage numbers
/// come from ClickHouse (empty when it is down — the pages still render), live
/// window counters from Redis, everything else from Postgres.
/// </summary>
[ApiController]
[Route("api/v1/admin")]
[Authorize(Policy = "OrgAdmin")]
public sealed class AdminOverviewController : ControllerBase
{
    private static readonly string[] AnomalyActions =
    {
        "login_failed", "otp_delivery_failed", "admin_login_without_otp",
        "provider_oauth_failed", "sessions_revoked", "api_key_revoked_by_admin"
    };

    private readonly GatewayDbContext _db;
    private readonly ClickHouseClient _ch;
    private readonly IConnectionMultiplexer _redis;

    public AdminOverviewController(GatewayDbContext db, ClickHouseClient ch, IConnectionMultiplexer redis)
    {
        _db = db; _ch = ch; _redis = redis;
    }

    // GET /api/v1/admin/organizations  (super admin: every tenant; org admin: their own)
    [HttpGet("organizations")]
    public async Task<IActionResult> Organizations(CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow.Date; // today, UTC

        // Organization has no tenant query filter (it IS the tenant), so scope it here:
        // super admins see every org, org admins only their own.
        var isSuper = User.IsInRole(nameof(Domain.Role.SuperAdmin));
        var ownOrg = Guid.Parse(User.FindFirstValue("org_id")!);
        var orgs = await _db.Organizations.AsNoTracking()
            .Where(o => isSuper || o.Id == ownOrg)
            .OrderBy(o => o.Name).Take(500).ToListAsync(ct);
        var orgIds = orgs.Select(o => o.Id).ToList();

        var seats = await _db.Users.AsNoTracking()
            .Where(u => orgIds.Contains(u.OrganizationId) && u.IsActive)
            .GroupBy(u => u.OrganizationId)
            .Select(g => new { OrgId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.OrgId, x => x.Count, ct);

        var workspaces = await _db.Workspaces.AsNoTracking()
            .Where(w => orgIds.Contains(w.OrganizationId) && w.IsActive)
            .Select(w => new { w.OrganizationId, w.QuotaPolicyJson })
            .ToListAsync(ct);

        // Daily limit = sum of each workspace's largest workspace-level window.
        var limits = workspaces
            .GroupBy(w => w.OrganizationId)
            .ToDictionary(g => g.Key, g => g.Sum(w =>
            {
                var policy = QuotaPolicyResolver.DeserializePolicy(w.QuotaPolicyJson) ?? QuotaPolicyResolver.DefaultPolicy();
                return policy.WorkspaceWindows.Length == 0 ? 0 : policy.WorkspaceWindows.Max(x => x.TokenLimit);
            }));

        var usage = (await _ch.GetOrgTotalsAsync(since, ct))
            .Where(u => Guid.TryParse(u.OrganizationId, out _))
            .ToDictionary(u => Guid.Parse(u.OrganizationId));

        var rows = orgs.Select(o =>
        {
            usage.TryGetValue(o.Id, out var u);
            var used = u?.TotalTokens ?? 0;
            var limit = limits.GetValueOrDefault(o.Id, 0);
            var ratio = limit > 0 ? (double)used / limit : 0;
            return new
            {
                id = o.Id,
                name = o.Name,
                slug = o.Slug,
                isActive = o.IsActive,
                seats = seats.GetValueOrDefault(o.Id, 0),
                used,
                limit,
                costToday = u?.CostUsd ?? 0m,
                status = !o.IsActive ? "throttled" : ratio >= 1 ? "throttled" : ratio >= 0.8 ? "near" : "active"
            };
        }).ToList();

        return Ok(new
        {
            orgs = rows,
            totals = new
            {
                orgs = rows.Count,
                employees = rows.Sum(r => r.seats),
                tokensToday = rows.Sum(r => r.used),
                costToday = rows.Sum(r => r.costToday)
            }
        });
    }

    // GET /api/v1/admin/anomalies  — security-relevant audit events, last 24h
    [HttpGet("anomalies")]
    public async Task<IActionResult> Anomalies([FromQuery] int hours = 24, [FromQuery] int limit = 50, CancellationToken ct = default)
    {
        hours = Math.Clamp(hours, 1, 24 * 30);
        limit = Math.Clamp(limit, 1, 200);
        var since = DateTimeOffset.UtcNow.AddHours(-hours);

        var events = await _db.AuditLogs.AsNoTracking()
            .Where(a => a.At >= since && AnomalyActions.Contains(a.Action))
            .OrderByDescending(a => a.At)
            .Take(limit)
            .ToListAsync(ct);

        var userIds = events.Where(e => e.UserId != null).Select(e => e.UserId!.Value).Distinct().ToList();
        var emails = await _db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Email, ct);

        return Ok(new
        {
            anomalies = events.Select(e => new
            {
                id = e.Id.ToString(),
                organizationId = e.OrganizationId,
                userEmail = e.UserId is { } uid && emails.TryGetValue(uid, out var em) ? em : (e.Action == "login_failed" ? e.Detail ?? "unknown" : "unknown"),
                kind = e.Action,
                detail = e.Detail ?? "",
                ip = e.Ip,
                at = e.At
            })
        });
    }

    // GET /api/v1/admin/workspaces/{id}/top-consumers — usage inside the workspace's current window
    [HttpGet("workspaces/{id:guid}/top-consumers")]
    public async Task<IActionResult> TopConsumers(Guid id, CancellationToken ct)
    {
        var ws = await _db.Workspaces.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
        if (ws is null) return NotFound(new { error = new { code = "workspace_not_found", message = "Workspace not found." } });

        var policy = QuotaPolicyResolver.DeserializePolicy(ws.QuotaPolicyJson) ?? QuotaPolicyResolver.DefaultPolicy();
        var wsWindow = policy.WorkspaceWindows.FirstOrDefault() ?? QuotaWindow.Daily(1_000_000);
        var userLimit = policy.UserWindows.FirstOrDefault()?.TokenLimit ?? 0;

        // Live counter for the workspace window (same key layout as QuotaEngine).
        var redis = _redis.GetDatabase();
        var key = $"quota:workspace:{id}:workspace:{wsWindow.Name}";
        var used = (long?)await redis.StringGetAsync(key) ?? 0;
        var ttl = await redis.KeyTimeToLiveAsync(key);

        var stats = await _ch.GetWorkspaceConsumersAsync(id, wsWindow.WindowMinutes, ct);
        var userIds = stats.Where(s => Guid.TryParse(s.UserId, out _)).Select(s => Guid.Parse(s.UserId)).ToList();
        var emails = await _db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Email, ct);

        var consumers = stats
            .Where(s => Guid.TryParse(s.UserId, out _))
            .Select(s =>
            {
                var uid = Guid.Parse(s.UserId);
                var total = s.InputTokens + s.OutputTokens;
                return new
                {
                    userId = uid,
                    email = emails.GetValueOrDefault(uid, "(removed user)"),
                    inputTokens = s.InputTokens,
                    outputTokens = s.OutputTokens,
                    pctOfQuota = userLimit > 0 ? Math.Round(100.0 * total / userLimit, 1) : 0,
                    costUsd = s.CostUsd
                };
            })
            .ToList();

        return Ok(new
        {
            workspaceId = id,
            name = ws.Name,
            consumers,
            window = new
            {
                name = wsWindow.Name,
                used,
                limit = wsWindow.TokenLimit,
                resetInSeconds = (int)(ttl?.TotalSeconds ?? wsWindow.WindowSeconds)
            }
        });
    }
}
