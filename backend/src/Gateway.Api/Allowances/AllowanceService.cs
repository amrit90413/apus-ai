using System.Data;
using System.Data.Common;
using Gateway.Api.Billing;
using Gateway.Api.Domain;
using Gateway.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Gateway.Api.Allowances;

/// <summary>Who an allowance is being checked for, resolved once per request.</summary>
public sealed record AllowanceOwner(
    Guid OrganizationId,
    Guid MembershipId,
    Guid UserId,
    Guid WorkspaceId,
    string Currency,
    long? UserMonthlyMinor,
    bool UserUnlimited,
    long? OrganizationMonthlyMinor,
    /// <summary>Optional per-day ceiling under the monthly one. Null = no daily cap.</summary>
    long? UserDailyMinor = null);

/// <summary>A held reservation. Every successful reserve must be settled exactly once.</summary>
public sealed record AllowanceReservation(
    Guid UserPeriodId,
    Guid OrganizationPeriodId,
    long AmountMinor,
    string Currency,
    Guid UserDailyPeriodId = default)
{
    public static readonly AllowanceReservation None = new(Guid.Empty, Guid.Empty, 0, "USD");

    public bool Held => AmountMinor > 0 &&
        (UserPeriodId != Guid.Empty || OrganizationPeriodId != Guid.Empty || UserDailyPeriodId != Guid.Empty);
}

public enum AllowanceOutcome { Allowed = 0, UserExceeded = 1, OrganizationExceeded = 2, UserDailyExceeded = 3 }

public sealed record AllowanceDecision(
    AllowanceOutcome Outcome,
    AllowanceReservation Reservation,
    Money UserRemaining,
    Money OrganizationRemaining,
    bool UserEnforced,
    bool OrganizationEnforced)
{
    public bool Allowed => Outcome == AllowanceOutcome.Allowed;
}

/// <summary>A period's live state, for dashboards and the allowance APIs.</summary>
public sealed record AllowanceSnapshot(
    Guid PeriodId,
    AllowanceScope Scope,
    string Currency,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    bool Unlimited,
    long AllocatedMinor,
    long AdjustmentMinor,
    long ConsumedMinor,
    long ReservedMinor,
    long RequestCount,
    long TokenCount)
{
    public long AvailableMinor => Unlimited ? long.MaxValue : AllocatedMinor + AdjustmentMinor - ConsumedMinor - ReservedMinor;
    public long BudgetMinor => AllocatedMinor + AdjustmentMinor;
    public Money Available => new(Math.Max(0, Unlimited ? 0 : AvailableMinor), Currency);
    public Money Consumed => new(ConsumedMinor, Currency);
    public Money Budget => new(BudgetMinor, Currency);
}

public interface IAllowanceService
{
    /// <summary>
    /// Atomically holds <paramref name="amount"/> against the user's period and then
    /// the organization's. If the organization has no room the user hold is released,
    /// so a rejected request leaves no residue.
    /// </summary>
    Task<AllowanceDecision> ReserveAsync(AllowanceOwner owner, Money amount, CancellationToken ct);

    /// <summary>Settles a reservation against the real cost. Never cancelled: a client disconnect must not skip it.</summary>
    Task SettleAsync(AllowanceReservation reservation, Money actual, long tokens, int requests = 1);

    /// <summary>Releases a reservation in full (the request never reached the provider).</summary>
    Task ReleaseAsync(AllowanceReservation reservation);

    Task<AllowanceSnapshot> UserPeriodAsync(AllowanceOwner owner, DateTimeOffset at, CancellationToken ct);
    Task<AllowanceSnapshot> OrganizationPeriodAsync(AllowanceOwner owner, DateTimeOffset at, CancellationToken ct);

    /// <summary>The member's day period, or null when they have no daily cap.</summary>
    Task<AllowanceSnapshot?> UserDailyPeriodAsync(AllowanceOwner owner, DateTimeOffset at, CancellationToken ct);

    /// <summary>Adds (or removes) budget within the current period without touching history.</summary>
    Task<AllowanceSnapshot> AdjustAsync(Guid periodId, long deltaMinor, CancellationToken ct);

    /// <summary>Sets the allocation of the current period, e.g. after an admin changes a member's monthly limit.</summary>
    Task<AllowanceSnapshot> SetAllocationAsync(Guid periodId, long allocatedMinor, bool unlimited, CancellationToken ct);
}

/// <summary>
/// Currency allowances with reservation accounting.
///
/// The race this closes: fifty concurrent requests against a ₹100 budget must not all
/// read "₹100 available" and proceed. Every request reserves its worst-case cost in a
/// single conditional UPDATE — the row is the lock — and settles to the real cost when
/// the provider is done, releasing whatever it over-held.
///
/// Postgres, not Redis, is the source of truth here: money must survive a cache flush.
/// </summary>
public sealed class AllowanceService : IAllowanceService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AllowanceService> _log;

    public AllowanceService(IServiceScopeFactory scopeFactory, ILogger<AllowanceService> log)
    {
        _scopeFactory = scopeFactory; _log = log;
    }

    // ----------------------------------------------------------------- reserve

    public async Task<AllowanceDecision> ReserveAsync(AllowanceOwner owner, Money amount, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

        var now = DateTimeOffset.UtcNow;
        var userPeriod = await EnsureAsync(db, owner, AllowanceScope.User, now, ct);
        var orgPeriod = await EnsureAsync(db, owner, AllowanceScope.Organization, now, ct);
        // Only opened when the member actually has a daily cap, so the common case
        // costs nothing.
        var dailyPeriod = owner.UserDailyMinor is null
            ? null
            : await EnsureAsync(db, owner, AllowanceScope.UserDaily, now, ct);

        var hold = Math.Max(0, amount.Minor);

        // Tightest ceiling first, so a request blocked by the day never disturbs the
        // month's or the tenant's counters.
        if (dailyPeriod is not null && await TryHoldAsync(db, dailyPeriod.PeriodId, hold, ct) is null)
        {
            var current = await SnapshotAsync(db, dailyPeriod.PeriodId, ct);
            return new AllowanceDecision(AllowanceOutcome.UserDailyExceeded, AllowanceReservation.None,
                current.Available, Money.Zero(owner.Currency), true, !orgPeriod.Unlimited);
        }

        var userOk = await TryHoldAsync(db, userPeriod.PeriodId, hold, ct);
        if (userOk is null)
        {
            await ReleaseDailyAsync(db, dailyPeriod, hold);
            var current = await SnapshotAsync(db, userPeriod.PeriodId, ct);
            return new AllowanceDecision(AllowanceOutcome.UserExceeded, AllowanceReservation.None,
                current.Available, Money.Zero(owner.Currency), !current.Unlimited, !orgPeriod.Unlimited);
        }

        var orgOk = await TryHoldAsync(db, orgPeriod.PeriodId, hold, ct);
        if (orgOk is null)
        {
            // Give the member's holds back before answering: otherwise a tenant that is
            // out of budget would slowly strangle every member's allowance too.
            await ReleaseOneAsync(db, userPeriod.PeriodId, hold, CancellationToken.None);
            await ReleaseDailyAsync(db, dailyPeriod, hold);
            var orgCurrent = await SnapshotAsync(db, orgPeriod.PeriodId, ct);
            return new AllowanceDecision(AllowanceOutcome.OrganizationExceeded, AllowanceReservation.None,
                new Money(Math.Max(0, userOk.Value), owner.Currency), orgCurrent.Available,
                !userPeriod.Unlimited, !orgPeriod.Unlimited);
        }

        return new AllowanceDecision(
            AllowanceOutcome.Allowed,
            new AllowanceReservation(userPeriod.PeriodId, orgPeriod.PeriodId, hold, owner.Currency,
                dailyPeriod?.PeriodId ?? Guid.Empty),
            new Money(Math.Max(0, userOk.Value), owner.Currency),
            new Money(Math.Max(0, orgOk.Value), owner.Currency),
            !userPeriod.Unlimited, !orgPeriod.Unlimited);
    }

    private static Task ReleaseDailyAsync(GatewayDbContext db, PeriodRef? daily, long hold) =>
        daily is null ? Task.CompletedTask : ReleaseOneAsync(db, daily.PeriodId, hold, CancellationToken.None);

    /// <summary>
    /// The atomic hold. One conditional UPDATE: an unlimited period always matches, a
    /// period without room matches no row. There is no read-then-write window.
    /// </summary>
    private static async Task<long?> TryHoldAsync(GatewayDbContext db, Guid periodId, long amount, CancellationToken ct)
    {
        const string sql = """
            UPDATE allowance_periods
               SET reserved_minor = reserved_minor + @amt, updated_at = now()
             WHERE id = @id
               AND (unlimited
                    OR allocated_minor + adjustment_minor - consumed_minor - reserved_minor >= @amt)
            RETURNING CASE WHEN unlimited THEN NULL
                           ELSE allocated_minor + adjustment_minor - consumed_minor - reserved_minor END
            """;

        await using var cmd = await CommandAsync(db, sql, ct, ("amt", amount), ("id", periodId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;           // no room
        return reader.IsDBNull(0) ? long.MaxValue : reader.GetInt64(0);
    }

    // ------------------------------------------------------------------ settle

    public async Task SettleAsync(AllowanceReservation reservation, Money actual, long tokens, int requests = 1)
    {
        if (!reservation.Held && actual.Minor == 0) return;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

        // Both periods settle in one transaction so a crash cannot leave the tenant
        // charged while the member is not, or vice versa.
        await using var tx = await db.Database.BeginTransactionAsync(CancellationToken.None);
        try
        {
            await SettleOneAsync(db, reservation.UserDailyPeriodId, reservation.AmountMinor, actual.Minor, tokens, requests);
            await SettleOneAsync(db, reservation.UserPeriodId, reservation.AmountMinor, actual.Minor, tokens, requests);
            await SettleOneAsync(db, reservation.OrganizationPeriodId, reservation.AmountMinor, actual.Minor, tokens, requests);
            await tx.CommitAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(CancellationToken.None);
            // Fail closed: the reservation stays held until the reconciliation worker
            // releases it, rather than handing back budget that may have been spent.
            _log.LogError(ex, "Allowance settle failed for periods {User}/{Org}; the reservation stays held.",
                reservation.UserPeriodId, reservation.OrganizationPeriodId);
        }
    }

    private static async Task SettleOneAsync(GatewayDbContext db, Guid periodId, long reserved, long actual, long tokens, int requests)
    {
        if (periodId == Guid.Empty) return;

        const string sql = """
            UPDATE allowance_periods
               SET reserved_minor = GREATEST(0, reserved_minor - @reserved),
                   consumed_minor = consumed_minor + @actual,
                   token_count    = token_count + @tokens,
                   request_count  = request_count + @requests,
                   updated_at     = now()
             WHERE id = @id
            """;
        await using var cmd = await CommandAsync(db, sql, CancellationToken.None,
            ("reserved", reserved), ("actual", actual), ("tokens", tokens), ("requests", requests), ("id", periodId));
        await cmd.ExecuteNonQueryAsync(CancellationToken.None);
    }

    public async Task ReleaseAsync(AllowanceReservation reservation)
    {
        if (!reservation.Held) return;
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        await ReleaseOneAsync(db, reservation.UserDailyPeriodId, reservation.AmountMinor, CancellationToken.None);
        await ReleaseOneAsync(db, reservation.UserPeriodId, reservation.AmountMinor, CancellationToken.None);
        await ReleaseOneAsync(db, reservation.OrganizationPeriodId, reservation.AmountMinor, CancellationToken.None);
    }

    private static async Task ReleaseOneAsync(GatewayDbContext db, Guid periodId, long amount, CancellationToken ct)
    {
        if (periodId == Guid.Empty || amount <= 0) return;
        const string sql = """
            UPDATE allowance_periods
               SET reserved_minor = GREATEST(0, reserved_minor - @amt), updated_at = now()
             WHERE id = @id
            """;
        await using var cmd = await CommandAsync(db, sql, ct, ("amt", amount), ("id", periodId));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ------------------------------------------------------------------ reads

    public async Task<AllowanceSnapshot> UserPeriodAsync(AllowanceOwner owner, DateTimeOffset at, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var period = await EnsureAsync(db, owner, AllowanceScope.User, at, ct);
        return await SnapshotAsync(db, period.PeriodId, ct);
    }

    public async Task<AllowanceSnapshot> OrganizationPeriodAsync(AllowanceOwner owner, DateTimeOffset at, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var period = await EnsureAsync(db, owner, AllowanceScope.Organization, at, ct);
        return await SnapshotAsync(db, period.PeriodId, ct);
    }

    public async Task<AllowanceSnapshot?> UserDailyPeriodAsync(AllowanceOwner owner, DateTimeOffset at, CancellationToken ct)
    {
        if (owner.UserDailyMinor is null) return null;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var period = await EnsureAsync(db, owner, AllowanceScope.UserDaily, at, ct);
        return await SnapshotAsync(db, period.PeriodId, ct);
    }

    public async Task<AllowanceSnapshot> AdjustAsync(Guid periodId, long deltaMinor, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

        const string sql = """
            UPDATE allowance_periods
               SET adjustment_minor = adjustment_minor + @delta, updated_at = now()
             WHERE id = @id
            """;
        await using var cmd = await CommandAsync(db, sql, ct, ("delta", deltaMinor), ("id", periodId));
        await cmd.ExecuteNonQueryAsync(ct);
        return await SnapshotAsync(db, periodId, ct);
    }

    public async Task<AllowanceSnapshot> SetAllocationAsync(Guid periodId, long allocatedMinor, bool unlimited, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

        const string sql = """
            UPDATE allowance_periods
               SET allocated_minor = @allocated, unlimited = @unlimited, updated_at = now()
             WHERE id = @id
            """;
        await using var cmd = await CommandAsync(db, sql, ct,
            ("allocated", allocatedMinor), ("unlimited", unlimited), ("id", periodId));
        await cmd.ExecuteNonQueryAsync(ct);
        return await SnapshotAsync(db, periodId, ct);
    }

    // ----------------------------------------------------------------- periods

    private sealed record PeriodRef(Guid PeriodId, bool Unlimited);

    /// <summary>
    /// Finds this owner's period row for the instant, creating it if the month has just
    /// turned over. Insert-then-select with ON CONFLICT DO NOTHING, so two pods opening
    /// the same period concurrently both end up on the same row.
    /// </summary>
    private async Task<PeriodRef> EnsureAsync(
        GatewayDbContext db, AllowanceOwner owner, AllowanceScope scope, DateTimeOffset at, CancellationToken ct)
    {
        var isDaily = scope == AllowanceScope.UserDaily;
        var (start, end) = isDaily ? AllowanceCalendar.Day(at) : AllowanceCalendar.Current(at);
        var isUser = scope != AllowanceScope.Organization;

        // A null budget means "track but do not cap": consumption still accrues so the
        // dashboards and the shared-pool view are accurate.
        var budget = scope switch
        {
            AllowanceScope.UserDaily => owner.UserDailyMinor,
            AllowanceScope.User => owner.UserUnlimited ? null : owner.UserMonthlyMinor,
            _ => owner.OrganizationMonthlyMinor,
        };
        var unlimited = budget is null;

        const string insert = """
            INSERT INTO allowance_periods
                (id, organization_id, scope, membership_id, user_id, workspace_id, currency,
                 period_start, period_end, allocated_minor, unlimited)
            VALUES (@id, @org, @scope, @membership, @user, @workspace, @currency,
                    @start, @end, @allocated, @unlimited)
            ON CONFLICT DO NOTHING
            """;

        await using (var cmd = await CommandAsync(db, insert, ct,
            ("id", Guid.NewGuid()), ("org", owner.OrganizationId), ("scope", (int)scope),
            ("membership", isUser ? owner.MembershipId : (object)DBNull.Value),
            ("user", isUser ? owner.UserId : (object)DBNull.Value),
            ("workspace", isUser ? owner.WorkspaceId : (object)DBNull.Value),
            ("currency", owner.Currency), ("start", start), ("end", end),
            ("allocated", budget ?? 0L), ("unlimited", unlimited)))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }

        const string select = """
            SELECT id, unlimited FROM allowance_periods
             WHERE organization_id = @org AND scope = @scope
               AND coalesce(membership_id, '00000000-0000-0000-0000-000000000000'::uuid) = @membership
               AND period_start = @start
            """;
        await using var read = await CommandAsync(db, select, ct,
            ("org", owner.OrganizationId), ("scope", (int)scope),
            ("membership", isUser ? owner.MembershipId : Guid.Empty), ("start", start));
        await using var reader = await read.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException("Allowance period could not be opened.");
        return new PeriodRef(reader.GetGuid(0), reader.GetBoolean(1));
    }

    private static async Task<AllowanceSnapshot> SnapshotAsync(GatewayDbContext db, Guid periodId, CancellationToken ct)
    {
        var row = await db.AllowancePeriods.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == periodId, ct)
            ?? throw new InvalidOperationException("Allowance period not found.");

        return new AllowanceSnapshot(row.Id, row.Scope, row.Currency, row.PeriodStart, row.PeriodEnd,
            row.Unlimited, row.AllocatedMinor, row.AdjustmentMinor, row.ConsumedMinor, row.ReservedMinor,
            row.RequestCount, row.TokenCount);
    }

    // ----------------------------------------------------------------- plumbing

    private static async Task<DbCommand> CommandAsync(
        GatewayDbContext db, string sql, CancellationToken ct, params (string name, object value)[] args)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);

        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        foreach (var (name, value) in args)
        {
            var prm = cmd.CreateParameter();
            prm.ParameterName = name;
            prm.Value = value;
            cmd.Parameters.Add(prm);
        }
        return cmd;
    }
}
