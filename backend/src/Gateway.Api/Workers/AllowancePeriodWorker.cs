using System.Data;
using System.Data.Common;
using Gateway.Api.Allowances;
using Gateway.Api.Billing;
using Gateway.Api.Domain;
using Gateway.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Workers;

/// <summary>
/// Looks after allowance periods:
///
///  - opens the next month's periods shortly before the rollover, so the first request
///    after midnight is not the one that has to create them;
///  - releases reservations abandoned by a pod that died mid-request, which would
///    otherwise hold budget hostage until the month ended;
///  - raises 50/75/90/100% notifications, exactly once each per period.
///
/// All three are scans, which is why they live here and not in the request path.
/// </summary>
public sealed class AllowancePeriodWorker : PeriodicWorker
{
    private readonly IServiceScopeFactory _scopeFactory;

    public AllowancePeriodWorker(IServiceScopeFactory scopeFactory, WorkerOptions options, ILogger<AllowancePeriodWorker> logger)
        : base(options, logger)
    {
        _scopeFactory = scopeFactory;
    }

    protected override string Name => nameof(AllowancePeriodWorker);
    protected override TimeSpan Interval => TimeSpan.FromSeconds(Options.AllowanceSweepSeconds);

    protected override async Task RunOnceAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

        await ReleaseAbandonedReservationsAsync(db, ct);
        await OpenNextPeriodsAsync(db, ct);
        await RaiseThresholdNotificationsAsync(db, ct);
    }

    /// <summary>
    /// A reservation is abandoned when its period has seen no allowance activity for
    /// longer than any request can last. Settlement always runs in a finally block, so
    /// the only way to get here is a process that was killed outright.
    /// </summary>
    private async Task ReleaseAbandonedReservationsAsync(GatewayDbContext db, CancellationToken ct)
    {
        var staleBefore = DateTimeOffset.UtcNow.AddMinutes(-Options.ReservationStaleMinutes);

        const string sql = """
            UPDATE allowance_periods
               SET reserved_minor = 0, updated_at = now()
             WHERE reserved_minor > 0 AND updated_at < @stale
            """;
        await using var cmd = await CommandAsync(db, sql, ct, ("stale", staleBefore));
        var released = await cmd.ExecuteNonQueryAsync(ct);
        if (released > 0)
            Logger.LogWarning("Released abandoned allowance reservations on {Count} period(s).", released);
    }

    /// <summary>
    /// Pre-creates next month's rows in the last hours of the current one. Purely an
    /// optimisation — the allowance service opens a period on demand — but it keeps the
    /// first request of the month off the slow path.
    /// </summary>
    private async Task OpenNextPeriodsAsync(GatewayDbContext db, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var (currentStart, currentEnd) = AllowanceCalendar.Current(now);
        if (currentEnd - now > TimeSpan.FromHours(6)) return;

        var (nextStart, nextEnd) = AllowanceCalendar.Next(now);

        // Carry each owner's configured budget forward, re-reading it from the
        // membership/organization so an allowance changed mid-month takes effect at the
        // rollover rather than being copied from the old period.
        const string sql = """
            INSERT INTO allowance_periods
                (id, organization_id, scope, membership_id, user_id, workspace_id, currency,
                 period_start, period_end, allocated_minor, unlimited)
            SELECT gen_random_uuid(), p.organization_id, p.scope, p.membership_id, p.user_id, p.workspace_id, p.currency,
                   @nextStart, @nextEnd,
                   CASE WHEN p.scope = 1 THEN coalesce(m.monthly_allowance_minor, 0)
                        ELSE coalesce(o.monthly_budget_minor, 0) END,
                   CASE WHEN p.scope = 1 THEN (m.unlimited_allowance OR m.monthly_allowance_minor IS NULL)
                        ELSE o.monthly_budget_minor IS NULL END
              FROM allowance_periods p
              JOIN organizations o ON o.id = p.organization_id
              LEFT JOIN memberships m ON m.id = p.membership_id
             -- Monthly scopes only. A daily period on the 1st shares the month's
             -- start date and would otherwise be rolled forward as a month-long row.
             WHERE p.period_start = @currentStart AND p.scope IN (0, 1)
            ON CONFLICT DO NOTHING
            """;

        await using var cmd = await CommandAsync(db, sql, ct,
            ("nextStart", nextStart), ("nextEnd", nextEnd), ("currentStart", currentStart));
        var opened = await cmd.ExecuteNonQueryAsync(ct);
        if (opened > 0)
            Logger.LogInformation("Opened {Count} allowance period(s) for {Period:yyyy-MM}.", opened, nextStart);
    }

    /// <summary>
    /// Claims each threshold with a conditional bitmask update, so exactly one replica
    /// queues the notification even when several run the sweep at the same moment.
    /// </summary>
    private async Task RaiseThresholdNotificationsAsync(GatewayDbContext db, CancellationToken ct)
    {
        var (start, _) = AllowanceCalendar.Current(DateTimeOffset.UtcNow);

        var periods = await db.AllowancePeriods.IgnoreQueryFilters().AsNoTracking()
            // Monthly scopes only: a daily cap resets at midnight and does not warrant
            // a "you have used 75% of your monthly allowance" message.
            .Where(p => p.PeriodStart == start && p.Scope != AllowanceScope.UserDaily
                        && !p.Unlimited && p.AllocatedMinor + p.AdjustmentMinor > 0)
            .Take(2000)
            .ToListAsync(ct);

        foreach (var period in periods)
        {
            if (ct.IsCancellationRequested) return;

            var budget = period.AllocatedMinor + period.AdjustmentMinor;
            var percent = (int)(period.ConsumedMinor * 100L / Math.Max(1, budget));

            for (var i = 0; i < Options.AllowanceThresholds.Count; i++)
            {
                var threshold = Options.AllowanceThresholds[i];
                if (percent < threshold) continue;

                var bit = 1 << i;
                if ((period.NotifiedThresholds & bit) != 0) continue;
                if (!await ClaimThresholdAsync(db, period.Id, bit, ct)) continue;

                await QueueThresholdNotificationAsync(db, period, threshold, budget, ct);
            }
        }
    }

    private static async Task<bool> ClaimThresholdAsync(GatewayDbContext db, Guid periodId, int bit, CancellationToken ct)
    {
        const string sql = """
            UPDATE allowance_periods
               SET notified_thresholds = notified_thresholds | @bit, updated_at = now()
             WHERE id = @id AND (notified_thresholds & @bit) = 0
            """;
        await using var cmd = await CommandAsync(db, sql, ct, ("bit", bit), ("id", periodId));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    private async Task QueueThresholdNotificationAsync(
        GatewayDbContext db, AllowancePeriod period, int threshold, long budgetMinor, CancellationToken ct)
    {
        var consumed = new Money(period.ConsumedMinor, period.Currency);
        var budget = new Money(budgetMinor, period.Currency);
        var isUser = period.Scope == AllowanceScope.User;

        var subject = isUser
            ? $"{threshold}% of your monthly AI allowance used"
            : $"{threshold}% of the organization's monthly AI budget used";

        var body = isUser
            ? $"You have used {consumed} of your {budget} monthly AI allowance ({threshold}%)."
            : $"Your organization has used {consumed} of its {budget} monthly AI budget ({threshold}%).";

        if (threshold >= 100)
            body += isUser
                ? " Further requests are blocked until your admin tops it up or the period resets."
                : " Further requests are blocked for everyone until the budget is raised or the period resets.";

        db.Notifications.Add(new NotificationOutboxEntry
        {
            OrganizationId = period.OrganizationId,
            UserId = isUser ? period.UserId : null,
            Kind = "allowance_threshold",
            Severity = threshold >= 100 ? "critical" : threshold >= 90 ? "warning" : "info",
            Subject = subject,
            Body = body,
            DedupeKey = $"allowance:{period.Id}:{threshold}",
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                periodId = period.Id,
                scope = period.Scope.ToString().ToLowerInvariant(),
                threshold,
                consumedMinor = period.ConsumedMinor,
                budgetMinor,
                currency = period.Currency,
            }),
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Another replica queued the same notification; the bitmask claim and this
            // index are two independent guards and either one is enough.
            db.ChangeTracker.Clear();
        }
    }

    private static async Task<DbCommand> CommandAsync(
        GatewayDbContext db, string sql, CancellationToken ct, params (string name, object value)[] args)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);

        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
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
