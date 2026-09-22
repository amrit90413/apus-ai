using System.Security.Claims;
using Gateway.Api.Allowances;
using Gateway.Api.Billing;
using Gateway.Api.Persistence;
using Gateway.Api.Providers;
using Gateway.Api.Quota;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace Gateway.Api.Gateway;

// Self-service endpoints backing `yourcompany-ai usage|sessions|models` and the user dashboard.
[ApiController]
[Route("api/v1/me")]
[Authorize]
public sealed class MeController : ControllerBase
{
    private readonly IQuotaPolicyResolver _policies;
    private readonly ITokenBalanceService _balances;
    private readonly IConnectionMultiplexer _redis;
    private readonly GatewayDbContext _db;
    private readonly IAllowanceService _allowances;
    private readonly IProviderConnectionService _connections;

    public MeController(
        IQuotaPolicyResolver policies, ITokenBalanceService balances, IConnectionMultiplexer redis,
        GatewayDbContext db, IAllowanceService allowances, IProviderConnectionService connections)
    {
        _policies = policies; _balances = balances; _redis = redis; _db = db;
        _allowances = allowances; _connections = connections;
    }

    private (Guid userId, Guid workspaceId, Guid sessionId) Identity()
    {
        return (
            Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!),
            Guid.Parse(User.FindFirstValue("workspace_id")!),
            Guid.Parse(User.FindFirstValue("session_id")!));
    }

    [HttpGet("usage")]
    public async Task<IActionResult> Usage(CancellationToken ct)
    {
        var (userId, workspaceId, _) = Identity();
        var policy = await _policies.ResolveAsync(new QuotaPrincipal(userId, workspaceId), ct);
        var db = _redis.GetDatabase();

        var windows = new List<object>();
        foreach (var w in policy.UserWindows)
        {
            var key = $"quota:user:{userId}:user:{w.Name}";
            var used = (long?)(await db.StringGetAsync(key)) ?? 0;
            var ttl = await db.KeyTimeToLiveAsync(key);
            windows.Add(new { name = w.Name, used, limit = w.TokenLimit, resetInSeconds = (int)(ttl?.TotalSeconds ?? w.WindowSeconds) });
        }

        // Credit a due allowance first, so the dashboard shows this month's balance
        // rather than last month's remainder. Cheap when nothing is owed.
        var principal = new QuotaPrincipal(userId, workspaceId);
        await _balances.EnsureAllowanceAsync(policy.OrganizationId, principal, ct);

        var balance = await _balances.GetAsync(policy.OrganizationId, principal, ct);
        return Ok(new { windows, balance = new { enforced = balance is not null, remaining = balance } });
    }

    /// <summary>Prepaid balance only — cheap enough for an IDE extension to poll after each reply.</summary>
    [HttpGet("balance")]
    public async Task<IActionResult> Balance(CancellationToken ct)
    {
        var (userId, workspaceId, _) = Identity();
        var principal = new QuotaPrincipal(userId, workspaceId);
        var policy = await _policies.ResolveAsync(principal, ct);
        await _balances.EnsureAllowanceAsync(policy.OrganizationId, principal, ct);

        var balance = await _balances.GetAsync(policy.OrganizationId, principal, ct);
        return Ok(new { enforced = balance is not null, remaining = balance });
    }

    [HttpGet("models")]
    public async Task<IActionResult> Models(CancellationToken ct)
    {
        var (userId, workspaceId, _) = Identity();
        var policy = await _policies.ResolveAsync(new QuotaPrincipal(userId, workspaceId), ct);
        return Ok(new { models = policy.AllowedModels });
    }

    /// <summary>
    /// What a member sees about their own AI use: this month's allowance, what they
    /// have spent, when it resets, and which models they can actually reach.
    ///
    /// Deliberately says nothing about how the organization authenticates to the
    /// provider — no connection id, no hint, no account name.
    /// </summary>
    [HttpGet("ai")]
    public async Task<IActionResult> Ai(CancellationToken ct)
    {
        var (userId, workspaceId, _) = Identity();
        var principal = new QuotaPrincipal(userId, workspaceId);
        var policy = await _policies.ResolveAsync(principal, ct);

        var owner = new AllowanceOwner(
            policy.OrganizationId, policy.MembershipId, policy.UserId, policy.WorkspaceId, policy.Currency,
            policy.UserMonthlyAllowanceMinor, policy.UserUnlimitedAllowance, policy.OrganizationMonthlyBudgetMinor);

        var period = await _allowances.UserPeriodAsync(owner, DateTimeOffset.UtcNow, ct);
        var connected = await _connections.ConnectedProvidersAsync(policy.OrganizationId, ct);
        var balance = await _balances.GetAsync(policy.OrganizationId, principal, ct);

        var models = policy.AllowedModels
            .Select(id => new
            {
                id,
                providers = ProviderCatalog.ProvidersForModel(id)
                    .Where(p => policy.AllowsProvider(p) && connected.Contains(p, StringComparer.OrdinalIgnoreCase))
                    .ToList(),
            })
            .Where(m => m.providers.Count > 0)
            .ToList();

        return Ok(new
        {
            currency = policy.Currency,
            allowance = new
            {
                unlimited = period.Unlimited,
                budgetMinor = period.BudgetMinor,
                usedMinor = period.ConsumedMinor,
                reservedMinor = period.ReservedMinor,
                remainingMinor = period.Unlimited ? (long?)null : Math.Max(0, period.AvailableMinor),
                periodStart = period.PeriodStart,
                // "Reset: 1 October" in the UI.
                resetsAt = period.PeriodEnd,
            },
            usage = new { requests = period.RequestCount, tokens = period.TokenCount },
            tokenBalance = new { enforced = balance is not null, remaining = balance },
            access = new
            {
                status = policy.AiStatus.ToString().ToLowerInvariant(),
                expiresAt = policy.AccessExpiresAt,
                organizationAiEnabled = policy.OrganizationAiEnabled,
            },
            limits = new
            {
                rpm = policy.RpmLimit,
                tpm = policy.TpmLimit,
                concurrency = policy.ConcurrencyLimit,
                dailyRequests = policy.DailyRequestLimit,
            },
            models,
        });
    }

    /// <summary>Allowance only — cheap enough for an IDE extension to poll after each reply.</summary>
    [HttpGet("allowance")]
    public async Task<IActionResult> Allowance(CancellationToken ct)
    {
        var (userId, workspaceId, _) = Identity();
        var policy = await _policies.ResolveAsync(new QuotaPrincipal(userId, workspaceId), ct);

        var period = await _allowances.UserPeriodAsync(new AllowanceOwner(
            policy.OrganizationId, policy.MembershipId, policy.UserId, policy.WorkspaceId, policy.Currency,
            policy.UserMonthlyAllowanceMinor, policy.UserUnlimitedAllowance, policy.OrganizationMonthlyBudgetMinor),
            DateTimeOffset.UtcNow, ct);

        return Ok(new
        {
            currency = policy.Currency,
            unlimited = period.Unlimited,
            budgetMinor = period.BudgetMinor,
            usedMinor = period.ConsumedMinor,
            remainingMinor = period.Unlimited ? (long?)null : Math.Max(0, period.AvailableMinor),
            resetsAt = period.PeriodEnd,
        });
    }

    [HttpGet("sessions")]
    public async Task<IActionResult> Sessions(CancellationToken ct)
    {
        var (userId, _, currentSessionId) = Identity();
        var sessions = await _db.Sessions
            .Where(s => s.UserId == userId && s.RevokedAt == null)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new { s.DeviceName, lastIp = s.LastIp, s.CreatedAt, current = s.Id == currentSessionId })
            .ToListAsync(ct);
        return Ok(new { sessions });
    }
}
