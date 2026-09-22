using System.Security.Claims;
using System.Text.Json;
using Gateway.Api.Allowances;
using Gateway.Api.Billing;
using Gateway.Api.Domain;
using Gateway.Api.Gateway;
using Gateway.Api.Persistence;
using Gateway.Api.Providers;
using Gateway.Api.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Admin;

public sealed record UpdateAiSettingsRequest(
    string? Currency,
    long? MonthlyBudgetMinor,
    bool? UnlimitedBudget,
    int? MarkupBps,
    bool? AiEnabled,
    string[]? AllowedProviders,
    string[]? AllowedModels);

/// <summary>
/// The tenant AI dashboard: budget, spend by provider and by member, model mix,
/// errors, latency and the daily trend — all read from the usage ledger, which is the
/// same rows billing reconciles against, so the dashboard and the invoice cannot
/// disagree.
/// </summary>
[ApiController]
[Route("api/v1/admin/ai")]
public sealed class AdminAiOverviewController : ControllerBase
{
    private const int MaxDays = 92;

    private readonly GatewayDbContext _db;
    private readonly IAllowanceService _allowances;
    private readonly IProviderConnectionService _connections;
    private readonly IPolicyCache _policyCache;
    private readonly IAuditWriter _audit;
    private readonly IFeatureFlags _features;

    public AdminAiOverviewController(
        GatewayDbContext db, IAllowanceService allowances, IProviderConnectionService connections,
        IPolicyCache policyCache, IAuditWriter audit, IFeatureFlags features)
    {
        _db = db; _allowances = allowances; _connections = connections;
        _policyCache = policyCache; _audit = audit; _features = features;
    }

    private Guid OrgId => Guid.Parse(User.FindFirstValue("org_id")!);

    // ------------------------------------------------------------------ overview

    [HttpGet("overview")]
    [RequirePermission(Permissions.UsageViewTenant)]
    public async Task<IActionResult> Overview(CancellationToken ct)
    {
        var org = await _db.Organizations.AsNoTracking().FirstAsync(o => o.Id == OrgId, ct);
        var currency = CurrencyInfo.Normalize(org.Currency);
        var (start, end) = AllowanceCalendar.Current(DateTimeOffset.UtcNow);

        var budget = await _allowances.OrganizationPeriodAsync(
            new AllowanceOwner(OrgId, Guid.Empty, Guid.Empty, Guid.Empty, currency, null, false, org.MonthlyBudgetMinor),
            DateTimeOffset.UtcNow, ct);

        // Only settled rows count towards spend; blocked and adjustment rows are
        // reported separately so a denial is never mistaken for consumption.
        var ledger = _db.UsageLedger.AsNoTracking().Where(u => u.CreatedAt >= start && u.CreatedAt < end);
        var billable = ledger.Where(u => u.Status == UsageStatus.Succeeded || u.Status == UsageStatus.Cancelled);

        var byProvider = await billable
            .GroupBy(u => u.Provider)
            .Select(g => new
            {
                provider = g.Key,
                customerCostMinor = g.Sum(x => x.CustomerCostMinor),
                providerCostMinor = g.Sum(x => x.ProviderCostMinor),
                requests = g.Count(),
                tokens = g.Sum(x => (long)x.InputTokens + x.OutputTokens + x.CachedInputTokens + x.CacheWriteTokens),
            })
            .OrderByDescending(x => x.customerCostMinor)
            .ToListAsync(ct);

        var byModel = await billable
            .GroupBy(u => new { u.Provider, u.Model })
            .Select(g => new
            {
                g.Key.Provider,
                g.Key.Model,
                customerCostMinor = g.Sum(x => x.CustomerCostMinor),
                requests = g.Count(),
                tokens = g.Sum(x => (long)x.InputTokens + x.OutputTokens + x.CachedInputTokens + x.CacheWriteTokens),
            })
            .OrderByDescending(x => x.customerCostMinor)
            .Take(50)
            .ToListAsync(ct);

        var byUserRaw = await billable
            .GroupBy(u => u.UserId)
            .Select(g => new
            {
                userId = g.Key,
                customerCostMinor = g.Sum(x => x.CustomerCostMinor),
                requests = g.Count(),
                tokens = g.Sum(x => (long)x.InputTokens + x.OutputTokens + x.CachedInputTokens + x.CacheWriteTokens),
            })
            .OrderByDescending(x => x.customerCostMinor)
            .Take(200)
            .ToListAsync(ct);

        var emails = await _db.Users.AsNoTracking()
            .Where(u => byUserRaw.Select(x => x.userId).Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Email, ct);

        var health = await billable
            .GroupBy(_ => 1)
            .Select(g => new
            {
                requests = g.Count(),
                tokens = g.Sum(x => (long)x.InputTokens + x.OutputTokens + x.CachedInputTokens + x.CacheWriteTokens),
                customerCostMinor = g.Sum(x => x.CustomerCostMinor),
                providerCostMinor = g.Sum(x => x.ProviderCostMinor),
                avgLatencyMs = g.Average(x => (double)x.LatencyMs),
            })
            .FirstOrDefaultAsync(ct);

        var failures = await ledger.CountAsync(u => u.Status == UsageStatus.Failed, ct);
        var blocked = await ledger.CountAsync(u => u.Status == UsageStatus.Blocked, ct);

        return Ok(new
        {
            currency,
            period = new { start, end },
            budget = new
            {
                unlimited = budget.Unlimited,
                budgetMinor = budget.BudgetMinor,
                consumedMinor = budget.ConsumedMinor,
                reservedMinor = budget.ReservedMinor,
                remainingMinor = budget.Unlimited ? (long?)null : Math.Max(0, budget.AvailableMinor),
            },
            totals = new
            {
                requests = health?.requests ?? 0,
                tokens = health?.tokens ?? 0,
                customerCostMinor = health?.customerCostMinor ?? 0,
                providerCostMinor = health?.providerCostMinor ?? 0,
                // Margin is the reason provider and customer cost are separate columns.
                marginMinor = (health?.customerCostMinor ?? 0) - (health?.providerCostMinor ?? 0),
                avgLatencyMs = (int)(health?.avgLatencyMs ?? 0),
                failures,
                blocked,
            },
            providers = byProvider,
            models = byModel,
            users = byUserRaw.Select(u => new
            {
                u.userId,
                email = emails.TryGetValue(u.userId, out var e) ? e : "(removed)",
                u.customerCostMinor,
                u.requests,
                u.tokens,
            }),
        });
    }

    // --------------------------------------------------------------------- trend

    [HttpGet("usage")]
    [RequirePermission(Permissions.UsageViewTenant)]
    public async Task<IActionResult> Usage([FromQuery] int days = 30, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, MaxDays);
        var since = DateTimeOffset.UtcNow.Date.AddDays(-(days - 1));
        var org = await _db.Organizations.AsNoTracking().FirstAsync(o => o.Id == OrgId, ct);

        var daily = await _db.UsageLedger.AsNoTracking()
            .Where(u => u.CreatedAt >= since && u.Status == UsageStatus.Succeeded)
            .GroupBy(u => u.BillingPeriod)
            .Select(g => new
            {
                date = g.Key,
                requests = g.Count(),
                tokens = g.Sum(x => (long)x.InputTokens + x.OutputTokens + x.CachedInputTokens + x.CacheWriteTokens),
                customerCostMinor = g.Sum(x => x.CustomerCostMinor),
                providerCostMinor = g.Sum(x => x.ProviderCostMinor),
            })
            .OrderBy(x => x.date)
            .ToListAsync(ct);

        return Ok(new { currency = CurrencyInfo.Normalize(org.Currency), days, daily });
    }

    /// <summary>Recent requests, for spot-checking a charge or an error.</summary>
    [HttpGet("requests")]
    [RequirePermission(Permissions.UsageViewTenant)]
    public async Task<IActionResult> Requests([FromQuery] Guid? userId, [FromQuery] int limit = 100, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 500);

        var query = _db.UsageLedger.AsNoTracking();
        if (userId is { } id) query = query.Where(u => u.UserId == id);

        var rows = await query
            .OrderByDescending(u => u.Id)
            .Take(limit)
            .Select(u => new
            {
                u.Id, u.RequestId, u.UserId, u.Provider, u.Model,
                u.InputTokens, u.OutputTokens, u.CachedInputTokens, u.CacheWriteTokens,
                u.ProviderCostMinor, u.CustomerCostMinor, u.Currency,
                u.LatencyMs, u.StartedAt, u.CompletedAt,
                Status = u.Status.ToString().ToLowerInvariant(),
                u.HttpStatus, u.FailureCategory, u.FallbackFrom,
            })
            .ToListAsync(ct);

        return Ok(new { requests = rows });
    }

    // ------------------------------------------------------------ provider view

    /// <summary>Connection state joined with this period's traffic, for the providers page.</summary>
    [HttpGet("providers")]
    [RequirePermission(Permissions.ProviderView)]
    public async Task<IActionResult> Providers(CancellationToken ct)
    {
        var org = await _db.Organizations.AsNoTracking().FirstAsync(o => o.Id == OrgId, ct);
        var currency = CurrencyInfo.Normalize(org.Currency);
        var (start, _) = AllowanceCalendar.Current(DateTimeOffset.UtcNow);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var connections = await _connections.ListAsync(OrgId, ct);

        var month = await _db.UsageLedger.AsNoTracking()
            .Where(u => u.CreatedAt >= start)
            .GroupBy(u => u.Provider)
            .Select(g => new
            {
                provider = g.Key,
                requests = g.Count(),
                tokens = g.Sum(x => (long)x.InputTokens + x.OutputTokens + x.CachedInputTokens + x.CacheWriteTokens),
                customerCostMinor = g.Sum(x => x.CustomerCostMinor),
                providerCostMinor = g.Sum(x => x.ProviderCostMinor),
                failures = g.Count(x => x.Status == UsageStatus.Failed),
                rateLimited = g.Count(x => x.HttpStatus == 429),
            })
            .ToDictionaryAsync(x => x.provider, ct);

        var day = await _db.UsageLedger.AsNoTracking()
            .Where(u => u.BillingPeriod == today)
            .GroupBy(u => u.Provider)
            .Select(g => new
            {
                provider = g.Key,
                requests = g.Count(),
                tokens = g.Sum(x => (long)x.InputTokens + x.OutputTokens + x.CachedInputTokens + x.CacheWriteTokens),
                customerCostMinor = g.Sum(x => x.CustomerCostMinor),
            })
            .ToDictionaryAsync(x => x.provider, ct);

        var rows = ProviderCatalog.Descriptors.Select(descriptor =>
        {
            var connection = connections.FirstOrDefault(c => c.Provider == descriptor.Id && c.Status != "revoked");
            month.TryGetValue(descriptor.Id, out var m);
            day.TryGetValue(descriptor.Id, out var d);

            return new
            {
                provider = descriptor.Id,
                displayName = descriptor.DisplayName,
                connection,     // never contains secret material — see ProviderConnectionView
                today = new
                {
                    requests = d?.requests ?? 0,
                    tokens = d?.tokens ?? 0,
                    costMinor = d?.customerCostMinor ?? 0,
                },
                month = new
                {
                    requests = m?.requests ?? 0,
                    tokens = m?.tokens ?? 0,
                    customerCostMinor = m?.customerCostMinor ?? 0,
                    providerCostMinor = m?.providerCostMinor ?? 0,
                    failures = m?.failures ?? 0,
                    rateLimited = m?.rateLimited ?? 0,
                },
            };
        });

        return Ok(new { currency, providers = rows });
    }

    // ------------------------------------------------------------------ settings

    [HttpGet("settings")]
    [RequirePermission(Permissions.ProviderView)]
    public async Task<IActionResult> GetSettings(CancellationToken ct)
    {
        var org = await _db.Organizations.AsNoTracking().FirstAsync(o => o.Id == OrgId, ct);
        var policy = QuotaPolicyResolver.DeserializeTenantPolicy(org.AiPolicyJson);

        return Ok(new
        {
            currency = CurrencyInfo.Normalize(org.Currency),
            monthlyBudgetMinor = org.MonthlyBudgetMinor,
            unlimitedBudget = org.MonthlyBudgetMinor is null,
            markupBps = org.MarkupBps,
            aiEnabled = org.AiEnabled,
            allowedProviders = policy?.AllowedProviders ?? Array.Empty<string>(),
            allowedModels = policy?.AllowedModels ?? Array.Empty<string>(),
            features = _features.Snapshot(OrgId),
        });
    }

    [HttpPut("settings")]
    [RequirePermission(Permissions.BillingManage)]
    public async Task<IActionResult> UpdateSettings([FromBody] UpdateAiSettingsRequest req, CancellationToken ct)
    {
        if (req.Currency is { } currency && !CurrencyInfo.IsKnown(currency))
            return BadRequest(new { error = new { code = "unknown_currency", message = $"'{currency}' is not a supported currency." } });
        if (req.MarkupBps is { } markup && markup is < 0 or > 100_000)
            return BadRequest(new { error = new { code = "invalid_markup", message = "markupBps must be between 0 and 100000." } });
        if (req.MonthlyBudgetMinor is { } budget && budget < 0)
            return BadRequest(new { error = new { code = "invalid_budget", message = "monthlyBudgetMinor cannot be negative." } });

        if (req.AllowedProviders is not null)
            foreach (var provider in req.AllowedProviders)
                if (ProviderCatalog.Normalize(provider) is null)
                    return BadRequest(new { error = new { code = "unknown_provider", message = $"'{provider}' is not a supported provider." } });

        var org = await _db.Organizations.FirstAsync(o => o.Id == OrgId, ct);
        var before = SettingsSnapshot(org);

        if (req.Currency is not null) org.Currency = CurrencyInfo.Normalize(req.Currency);
        if (req.UnlimitedBudget == true) org.MonthlyBudgetMinor = null;
        else if (req.MonthlyBudgetMinor is not null) org.MonthlyBudgetMinor = req.MonthlyBudgetMinor;
        if (req.MarkupBps is not null) org.MarkupBps = req.MarkupBps.Value;
        if (req.AiEnabled is not null) org.AiEnabled = req.AiEnabled.Value;

        if (req.AllowedProviders is not null || req.AllowedModels is not null)
        {
            var existing = QuotaPolicyResolver.DeserializeTenantPolicy(org.AiPolicyJson);
            var policy = new TenantAiPolicy(
                req.AllowedProviders is null ? existing?.AllowedProviders
                    : req.AllowedProviders.Length == 0 ? null : req.AllowedProviders.Select(p => ProviderCatalog.Normalize(p)!).Distinct().ToArray(),
                req.AllowedModels is null ? existing?.AllowedModels
                    : req.AllowedModels.Length == 0 ? null : req.AllowedModels);
            org.AiPolicyJson = policy is { AllowedProviders: null, AllowedModels: null }
                ? null
                : JsonSerializer.Serialize(policy, QuotaPolicyResolver.JsonOptions);
        }

        _audit.Record(AuditActions.OrgSettingsChanged, AuditResources.Organization, org.Id.ToString(),
            before, SettingsSnapshot(org), "AI settings updated");
        await _db.SaveChangesAsync(ct);

        // Every member's cached policy embeds the tenant currency, budget and
        // restrictions, so they all have to go.
        _policyCache.InvalidateOrganization(OrgId);

        return await GetSettings(ct);
    }

    private static object SettingsSnapshot(Organization org) => new
    {
        org.Currency,
        org.MonthlyBudgetMinor,
        org.MarkupBps,
        org.AiEnabled,
        org.AiPolicyJson,
    };
}
