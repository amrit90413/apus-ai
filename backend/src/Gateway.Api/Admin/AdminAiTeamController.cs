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

/// <summary>Set a member's monthly AI allowance. Amounts are minor units of the organization's currency.</summary>
public sealed record SetAllowanceRequest(long? MonthlyMinor, long? DailyMinor, bool? Unlimited, string? Note);

public sealed record TopUpAllowanceRequest(long AmountMinor, string? Note);

public sealed record SetLimitsRequest(int? Rpm, int? Tpm, int? Concurrency, int? DailyRequests);

public sealed record SetModelAccessRequest(string[]? AllowedModels, string[]? AllowedProviders);

public sealed record SetAccessRequest(string? Status, DateTimeOffset? ExpiresAt);

/// <summary>
/// The team page's API: who can use AI, how much they may spend, which models and
/// providers they may reach, and what they have used.
///
/// Every write is audited with before/after and evicts the policy cache, so a change
/// an admin makes here is enforced on the next request rather than at the next
/// deployment.
/// </summary>
[ApiController]
[Route("api/v1/admin/ai/users")]
public sealed class AdminAiTeamController : ControllerBase
{
    private const long MaxMinor = 1_000_000_000_000; // sanity bound, not a business limit

    private readonly GatewayDbContext _db;
    private readonly IAllowanceService _allowances;
    private readonly IPolicyCache _policyCache;
    private readonly IAuditWriter _audit;

    public AdminAiTeamController(
        GatewayDbContext db, IAllowanceService allowances, IPolicyCache policyCache, IAuditWriter audit)
    {
        _db = db; _allowances = allowances; _policyCache = policyCache; _audit = audit;
    }

    private Guid OrgId => Guid.Parse(User.FindFirstValue("org_id")!);

    // ---------------------------------------------------------------------- list

    /// <summary>Every member with their allowance, usage this period, limits and model access.</summary>
    [HttpGet]
    [RequirePermission(Permissions.AllowanceView)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var org = await _db.Organizations.AsNoTracking().FirstAsync(o => o.Id == OrgId, ct);
        var currency = CurrencyInfo.Normalize(org.Currency);
        var (periodStart, periodEnd) = AllowanceCalendar.Current(DateTimeOffset.UtcNow);

        var members = await _db.Memberships.AsNoTracking()
            .Join(_db.Users.AsNoTracking(), m => m.UserId, u => u.Id, (m, u) => new { m, u })
            .OrderBy(x => x.u.Email)
            .Take(1000)
            .ToListAsync(ct);

        var membershipIds = members.Select(x => x.m.Id).ToList();
        var periods = await _db.AllowancePeriods.AsNoTracking()
            .Where(p => p.Scope == AllowanceScope.User && p.PeriodStart == periodStart && membershipIds.Contains(p.MembershipId!.Value))
            .ToDictionaryAsync(p => p.MembershipId!.Value, ct);

        var rows = members.Select(x =>
        {
            periods.TryGetValue(x.m.Id, out var period);
            var budgetMinor = period is null
                ? x.m.MonthlyAllowanceMinor ?? 0
                : period.AllocatedMinor + period.AdjustmentMinor;
            var consumedMinor = period?.ConsumedMinor ?? 0;
            var unlimited = period?.Unlimited ?? (x.m.UnlimitedAllowance || x.m.MonthlyAllowanceMinor is null);

            return new
            {
                userId = x.u.Id,
                email = x.u.Email,
                workspaceId = x.m.WorkspaceId,
                membershipId = x.m.Id,
                role = x.m.Role.ToString(),
                isActive = x.u.IsActive,
                aiStatus = x.m.AiStatus.ToString().ToLowerInvariant(),
                accessExpiresAt = x.m.AccessExpiresAt,
                currency,
                unlimited,
                monthlyAllowanceMinor = x.m.MonthlyAllowanceMinor,
                dailyAllowanceMinor = x.m.DailyAllowanceMinor,
                budgetMinor,
                consumedMinor,
                reservedMinor = period?.ReservedMinor ?? 0,
                remainingMinor = unlimited ? (long?)null : Math.Max(0, budgetMinor - consumedMinor - (period?.ReservedMinor ?? 0)),
                requests = period?.RequestCount ?? 0,
                tokens = period?.TokenCount ?? 0,
                tokenBalance = x.m.TokenBalance,
                limits = new
                {
                    rpm = x.m.RpmLimit,
                    tpm = x.m.TpmLimit,
                    concurrency = x.m.ConcurrencyLimit,
                    dailyRequests = x.m.DailyRequestLimit,
                },
                allowedModels = Deserialize(x.m.PerUserQuotaJson) ?? Array.Empty<string>(),
                allowedProviders = DeserializeArray(x.m.AllowedProvidersJson) ?? Array.Empty<string>(),
            };
        }).ToList();

        return Ok(new
        {
            currency,
            period = new { start = periodStart, end = periodEnd },
            organization = new
            {
                monthlyBudgetMinor = org.MonthlyBudgetMinor,
                unlimited = org.MonthlyBudgetMinor is null,
                allocatedToMembersMinor = rows.Where(r => !r.unlimited).Sum(r => r.budgetMinor),
            },
            users = rows,
        });
    }

    // ----------------------------------------------------------------- allowance

    /// <summary>
    /// Sets the member's monthly allowance. The current period is updated too, so the
    /// change applies now rather than at the next rollover — raising a limit unblocks
    /// someone immediately, which is the whole point of the admin doing it.
    /// </summary>
    [HttpPut("{userId:guid}/allowance")]
    [RequirePermission(Permissions.AllowanceUpdate)]
    public async Task<IActionResult> SetAllowance(Guid userId, [FromQuery] Guid? workspaceId, [FromBody] SetAllowanceRequest req, CancellationToken ct)
    {
        if (req.MonthlyMinor is { } m && m is < 0 or > MaxMinor) return Invalid("invalid_amount", "monthlyMinor is out of range.");
        if (req.DailyMinor is { } d && d is < 0 or > MaxMinor) return Invalid("invalid_amount", "dailyMinor is out of range.");

        var (membership, error) = await MembershipLookup.ResolveAsync(_db, userId, workspaceId, ct);
        if (error is not null) return error;

        var before = Snapshot(membership!);
        membership!.MonthlyAllowanceMinor = req.MonthlyMinor;
        membership.DailyAllowanceMinor = req.DailyMinor;
        if (req.Unlimited is { } unlimited) membership.UnlimitedAllowance = unlimited;

        _audit.Record(AuditActions.AllowanceChanged, AuditResources.Allowance, membership.Id.ToString(),
            before, Snapshot(membership), req.Note);
        await _db.SaveChangesAsync(ct);
        _policyCache.InvalidateUser(userId, membership.WorkspaceId);

        var snapshot = await SyncCurrentPeriodAsync(membership, ct);
        return Ok(Shape(membership, snapshot));
    }

    /// <summary>Adds (or, with a negative amount, removes) budget inside the current period only.</summary>
    [HttpPost("{userId:guid}/allowance/top-up")]
    [RequirePermission(Permissions.AllowanceUpdate)]
    public async Task<IActionResult> TopUp(Guid userId, [FromQuery] Guid? workspaceId, [FromBody] TopUpAllowanceRequest req, CancellationToken ct)
    {
        if (req.AmountMinor == 0 || Math.Abs(req.AmountMinor) > MaxMinor)
            return Invalid("invalid_amount", "amountMinor must be non-zero and within range.");

        var (membership, error) = await MembershipLookup.ResolveAsync(_db, userId, workspaceId, ct);
        if (error is not null) return error;

        var owner = await OwnerAsync(membership!, ct);
        var period = await _allowances.UserPeriodAsync(owner, DateTimeOffset.UtcNow, ct);
        var updated = await _allowances.AdjustAsync(period.PeriodId, req.AmountMinor, ct);

        await _audit.WriteAsync(AuditActions.AllowanceTopUp, AuditResources.Allowance, membership!.Id.ToString(),
            new { adjustmentMinor = period.AdjustmentMinor },
            new { adjustmentMinor = updated.AdjustmentMinor },
            req.Note, ct);

        return Ok(Shape(membership, updated));
    }

    /// <summary>
    /// Clears the period's consumption. Deliberately an explicit admin action, recorded
    /// as such: it is a correction, not something that happens on a schedule.
    /// </summary>
    [HttpPost("{userId:guid}/allowance/reset")]
    [RequirePermission(Permissions.AllowanceUpdate)]
    public async Task<IActionResult> Reset(Guid userId, [FromQuery] Guid? workspaceId, CancellationToken ct)
    {
        var (membership, error) = await MembershipLookup.ResolveAsync(_db, userId, workspaceId, ct);
        if (error is not null) return error;

        var owner = await OwnerAsync(membership!, ct);
        var period = await _allowances.UserPeriodAsync(owner, DateTimeOffset.UtcNow, ct);

        // History is preserved: the offset is an adjustment, so the ledger and the
        // period's consumed figure both remain true.
        var updated = await _allowances.AdjustAsync(period.PeriodId, period.ConsumedMinor, ct);

        await _audit.WriteAsync(AuditActions.AllowanceReset, AuditResources.Allowance, membership!.Id.ToString(),
            new { consumedMinor = period.ConsumedMinor },
            new { adjustmentMinor = updated.AdjustmentMinor },
            "allowance reset by admin", ct);

        return Ok(Shape(membership, updated));
    }

    // -------------------------------------------------------------------- limits

    [HttpPut("{userId:guid}/limits")]
    [RequirePermission(Permissions.AllowanceUpdate)]
    public async Task<IActionResult> SetLimits(Guid userId, [FromQuery] Guid? workspaceId, [FromBody] SetLimitsRequest req, CancellationToken ct)
    {
        foreach (var value in new[] { req.Rpm, req.Tpm, req.Concurrency, req.DailyRequests })
            if (value is { } v && v is < 0 or > 1_000_000)
                return Invalid("invalid_limit", "Limits must be between 0 and 1,000,000.");

        var (membership, error) = await MembershipLookup.ResolveAsync(_db, userId, workspaceId, ct);
        if (error is not null) return error;

        var before = Snapshot(membership!);
        membership!.RpmLimit = req.Rpm;
        membership.TpmLimit = req.Tpm;
        membership.ConcurrencyLimit = req.Concurrency;
        membership.DailyRequestLimit = req.DailyRequests;

        _audit.Record(AuditActions.AllowanceChanged, AuditResources.Membership, membership.Id.ToString(),
            before, Snapshot(membership), "limits updated");
        await _db.SaveChangesAsync(ct);
        _policyCache.InvalidateUser(userId, membership.WorkspaceId);

        return Ok(new { userId, limits = new { rpm = membership.RpmLimit, tpm = membership.TpmLimit, concurrency = membership.ConcurrencyLimit, dailyRequests = membership.DailyRequestLimit } });
    }

    // -------------------------------------------------------------- model access

    /// <summary>
    /// Restricts which models and providers this member may use. These narrow the
    /// tenant and workspace policies — they can never widen them, so an admin cannot
    /// grant access the organization itself does not have.
    /// </summary>
    [HttpPut("{userId:guid}/models")]
    [RequirePermission(Permissions.ModelAccessUpdate)]
    public async Task<IActionResult> SetModelAccess(Guid userId, [FromQuery] Guid? workspaceId, [FromBody] SetModelAccessRequest req, CancellationToken ct)
    {
        if (req.AllowedModels is null && req.AllowedProviders is null)
            return Invalid("empty_request", "Provide allowedModels, allowedProviders, or both.");
        if (req.AllowedModels is { Length: > 64 } || req.AllowedProviders is { Length: > 16 })
            return Invalid("too_many_entries", "At most 64 models and 16 providers.");

        if (req.AllowedProviders is not null)
            foreach (var provider in req.AllowedProviders)
                if (ProviderCatalog.Normalize(provider) is null)
                    return Invalid("unknown_provider", $"'{provider}' is not a supported provider.");

        var (membership, error) = await MembershipLookup.ResolveAsync(_db, userId, workspaceId, ct);
        if (error is not null) return error;

        var before = Snapshot(membership!);

        if (req.AllowedModels is not null)
        {
            var existing = QuotaPolicyResolver.DeserializeOverride(membership!.PerUserQuotaJson);
            var payload = new UserOverride(existing?.UserWindows, req.AllowedModels.Length == 0 ? null : req.AllowedModels);
            membership.PerUserQuotaJson = payload is { UserWindows: null, AllowedModels: null }
                ? null
                : JsonSerializer.Serialize(payload, QuotaPolicyResolver.JsonOptions);
        }

        if (req.AllowedProviders is not null)
            membership!.AllowedProvidersJson = req.AllowedProviders.Length == 0
                ? null
                : JsonSerializer.Serialize(req.AllowedProviders.Select(p => ProviderCatalog.Normalize(p)!).Distinct());

        _audit.Record(AuditActions.ModelAccessChanged, AuditResources.ModelAccess, membership!.Id.ToString(),
            before, Snapshot(membership), "model/provider access updated");
        await _db.SaveChangesAsync(ct);
        _policyCache.InvalidateUser(userId, membership.WorkspaceId);

        return Ok(new
        {
            userId,
            allowedModels = Deserialize(membership.PerUserQuotaJson) ?? Array.Empty<string>(),
            allowedProviders = DeserializeArray(membership.AllowedProvidersJson) ?? Array.Empty<string>(),
        });
    }

    // ------------------------------------------------------------------- access

    /// <summary>Suspend, disable or reactivate a member's AI access, and set an expiry.</summary>
    [HttpPut("{userId:guid}/access")]
    [HttpPost("{userId:guid}/suspend")]
    [HttpPost("{userId:guid}/reactivate")]
    [RequirePermission(Permissions.AiUserSuspend)]
    public async Task<IActionResult> SetAccess(Guid userId, [FromQuery] Guid? workspaceId, [FromBody] SetAccessRequest? req, CancellationToken ct)
    {
        var path = Request.Path.Value ?? "";
        var status = path.EndsWith("/suspend", StringComparison.OrdinalIgnoreCase) ? AiAccessStatus.Suspended
            : path.EndsWith("/reactivate", StringComparison.OrdinalIgnoreCase) ? AiAccessStatus.Active
            : ParseStatus(req?.Status);

        if (status is null) return Invalid("invalid_status", "status must be active, suspended or disabled.");

        var (membership, error) = await MembershipLookup.ResolveAsync(_db, userId, workspaceId, ct);
        if (error is not null) return error;

        var before = Snapshot(membership!);
        membership!.AiStatus = status.Value;
        if (req?.ExpiresAt is not null || status == AiAccessStatus.Active)
            membership.AccessExpiresAt = req?.ExpiresAt;

        var action = status switch
        {
            AiAccessStatus.Suspended => AuditActions.MemberSuspended,
            AiAccessStatus.Disabled => AuditActions.MemberDisabled,
            _ => AuditActions.MemberReactivated,
        };
        _audit.Record(action, AuditResources.Membership, membership.Id.ToString(), before, Snapshot(membership));
        await _db.SaveChangesAsync(ct);
        _policyCache.InvalidateUser(userId, membership.WorkspaceId);

        return Ok(new { userId, aiStatus = membership.AiStatus.ToString().ToLowerInvariant(), accessExpiresAt = membership.AccessExpiresAt });
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Pushes a changed monthly allowance into the open period. Without this an admin
    /// would raise someone's limit and watch them stay blocked until the 1st.
    /// </summary>
    private async Task<AllowanceSnapshot> SyncCurrentPeriodAsync(Membership membership, CancellationToken ct)
    {
        var owner = await OwnerAsync(membership, ct);
        var period = await _allowances.UserPeriodAsync(owner, DateTimeOffset.UtcNow, ct);
        var unlimited = membership.UnlimitedAllowance || membership.MonthlyAllowanceMinor is null;
        return await _allowances.SetAllocationAsync(period.PeriodId, membership.MonthlyAllowanceMinor ?? 0, unlimited, ct);
    }

    private async Task<AllowanceOwner> OwnerAsync(Membership membership, CancellationToken ct)
    {
        var org = await _db.Organizations.AsNoTracking().FirstAsync(o => o.Id == OrgId, ct);
        return new AllowanceOwner(
            OrgId, membership.Id, membership.UserId, membership.WorkspaceId,
            CurrencyInfo.Normalize(org.Currency),
            membership.MonthlyAllowanceMinor, membership.UnlimitedAllowance, org.MonthlyBudgetMinor);
    }

    private static object Shape(Membership membership, AllowanceSnapshot period) => new
    {
        userId = membership.UserId,
        workspaceId = membership.WorkspaceId,
        currency = period.Currency,
        unlimited = period.Unlimited,
        monthlyAllowanceMinor = membership.MonthlyAllowanceMinor,
        dailyAllowanceMinor = membership.DailyAllowanceMinor,
        budgetMinor = period.BudgetMinor,
        consumedMinor = period.ConsumedMinor,
        reservedMinor = period.ReservedMinor,
        remainingMinor = period.Unlimited ? (long?)null : Math.Max(0, period.AvailableMinor),
        periodStart = period.PeriodStart,
        periodEnd = period.PeriodEnd,
    };

    /// <summary>Audit snapshot. Contains no secrets — memberships never hold credential material.</summary>
    private static object Snapshot(Membership m) => new
    {
        m.MonthlyAllowanceMinor,
        m.DailyAllowanceMinor,
        m.UnlimitedAllowance,
        m.RpmLimit,
        m.TpmLimit,
        m.ConcurrencyLimit,
        m.DailyRequestLimit,
        AiStatus = m.AiStatus.ToString(),
        m.AccessExpiresAt,
        AllowedModels = Deserialize(m.PerUserQuotaJson),
        AllowedProviders = DeserializeArray(m.AllowedProvidersJson),
    };

    private static string[]? Deserialize(string? perUserQuotaJson) =>
        QuotaPolicyResolver.DeserializeOverride(perUserQuotaJson)?.AllowedModels;

    private static string[]? DeserializeArray(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<string[]>(json, QuotaPolicyResolver.JsonOptions);

    private static AiAccessStatus? ParseStatus(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "active" => AiAccessStatus.Active,
        "suspended" => AiAccessStatus.Suspended,
        "disabled" => AiAccessStatus.Disabled,
        _ => null,
    };

    private IActionResult Invalid(string code, string message) =>
        BadRequest(new { error = new { code, message } });
}
