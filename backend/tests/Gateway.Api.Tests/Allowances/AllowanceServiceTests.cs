using Gateway.Api.Allowances;
using Gateway.Api.Billing;
using Gateway.Api.Domain;
using Gateway.Api.Persistence;
using Gateway.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gateway.Api.Tests.Allowances;

/// <summary>
/// The allowance engine against real Postgres. These cannot be proven with a fake: the
/// guarantee under test is that a single conditional UPDATE is the only thing standing
/// between fifty concurrent requests and an overspent budget.
/// </summary>
public sealed class AllowanceServiceTests : IAsyncLifetime
{
    private readonly PostgresDatabase _db = new();
    private AllowanceService _allowances = null!;

    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid WorkspaceId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid MembershipId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        if (!TestInfra.HasPostgres) return;

        _allowances = new AllowanceService(_db.ScopeFactory(), NullLogger<AllowanceService>.Instance);
        await SeedAsync();
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private async Task SeedAsync()
    {
        await using var db = _db.NewContext();
        db.Organizations.Add(new Organization { Id = OrgId, Name = "Acme", Slug = "acme-" + Guid.NewGuid().ToString("N")[..6], Currency = "INR" });
        db.Workspaces.Add(new Workspace { Id = WorkspaceId, OrganizationId = OrgId, Name = "Engineering" });
        db.Users.Add(new User { Id = UserId, OrganizationId = OrgId, Email = "dev@acme.test", PasswordHash = "x" });
        db.Memberships.Add(new Membership { Id = MembershipId, OrganizationId = OrgId, UserId = UserId, WorkspaceId = WorkspaceId });
        await db.SaveChangesAsync();
    }

    private static AllowanceOwner Owner(long? userBudget, long? orgBudget, bool userUnlimited = false, long? dailyBudget = null) =>
        new(OrgId, MembershipId, UserId, WorkspaceId, "INR", userBudget, userUnlimited, orgBudget, dailyBudget);

    // -------------------------------------------------------------- basic flow

    [PostgresFact]
    public async Task Reserve_then_settle_charges_the_real_cost_and_returns_the_rest()
    {
        // ₹100 budget, reserve ₹20 worst case, actually spend ₹5.
        var owner = Owner(userBudget: 10_000, orgBudget: 50_000);

        var decision = await _allowances.ReserveAsync(owner, new Money(2_000, "INR"), default);
        Assert.True(decision.Allowed);
        Assert.Equal(8_000, decision.UserRemaining.Minor);

        await _allowances.SettleAsync(decision.Reservation, new Money(500, "INR"), tokens: 1234);

        var period = await _allowances.UserPeriodAsync(owner, DateTimeOffset.UtcNow, default);
        Assert.Equal(500, period.ConsumedMinor);
        Assert.Equal(0, period.ReservedMinor);
        Assert.Equal(9_500, period.AvailableMinor);
        Assert.Equal(1234, period.TokenCount);
        Assert.Equal(1, period.RequestCount);
    }

    [PostgresFact]
    public async Task Release_hands_the_whole_reservation_back()
    {
        var owner = Owner(userBudget: 10_000, orgBudget: 50_000);

        var decision = await _allowances.ReserveAsync(owner, new Money(3_000, "INR"), default);
        await _allowances.ReleaseAsync(decision.Reservation);

        var period = await _allowances.UserPeriodAsync(owner, DateTimeOffset.UtcNow, default);
        Assert.Equal(0, period.ReservedMinor);
        Assert.Equal(0, period.ConsumedMinor);
        Assert.Equal(10_000, period.AvailableMinor);
    }

    [PostgresFact]
    public async Task A_reservation_reduces_what_the_next_request_can_see()
    {
        // The spec's example: balance ₹100, two requests reserving ₹20 each leave ₹60
        // available — not ₹100.
        var owner = Owner(userBudget: 10_000, orgBudget: 100_000);

        var first = await _allowances.ReserveAsync(owner, new Money(2_000, "INR"), default);
        var second = await _allowances.ReserveAsync(owner, new Money(2_000, "INR"), default);

        Assert.True(first.Allowed);
        Assert.True(second.Allowed);
        Assert.Equal(6_000, second.UserRemaining.Minor);
    }

    [PostgresFact]
    public async Task A_request_larger_than_the_remaining_budget_is_refused()
    {
        var owner = Owner(userBudget: 1_000, orgBudget: 100_000);

        var decision = await _allowances.ReserveAsync(owner, new Money(1_001, "INR"), default);

        Assert.False(decision.Allowed);
        Assert.Equal(AllowanceOutcome.UserExceeded, decision.Outcome);
        Assert.False(decision.Reservation.Held);
    }

    [PostgresFact]
    public async Task An_exhausted_tenant_budget_releases_the_user_hold()
    {
        // Otherwise a tenant that is out of budget would slowly eat every member's
        // allowance with holds that are never settled.
        var owner = Owner(userBudget: 100_000, orgBudget: 500);

        var decision = await _allowances.ReserveAsync(owner, new Money(1_000, "INR"), default);

        Assert.False(decision.Allowed);
        Assert.Equal(AllowanceOutcome.OrganizationExceeded, decision.Outcome);

        var userPeriod = await _allowances.UserPeriodAsync(owner, DateTimeOffset.UtcNow, default);
        Assert.Equal(0, userPeriod.ReservedMinor);
    }

    [PostgresFact]
    public async Task An_unlimited_member_still_accrues_consumption_for_reporting()
    {
        var owner = Owner(userBudget: null, orgBudget: 100_000);

        var decision = await _allowances.ReserveAsync(owner, new Money(5_000, "INR"), default);
        Assert.True(decision.Allowed);
        await _allowances.SettleAsync(decision.Reservation, new Money(1_500, "INR"), tokens: 99);

        var period = await _allowances.UserPeriodAsync(owner, DateTimeOffset.UtcNow, default);
        Assert.True(period.Unlimited);
        Assert.Equal(1_500, period.ConsumedMinor);   // tracked, not capped
    }

    [PostgresFact]
    public async Task Consumption_beyond_the_budget_blocks_the_next_request_rather_than_going_negative()
    {
        var owner = Owner(userBudget: 1_000, orgBudget: 100_000);

        var decision = await _allowances.ReserveAsync(owner, new Money(1_000, "INR"), default);
        Assert.True(decision.Allowed);
        // The provider produced more than the estimate allowed for.
        await _allowances.SettleAsync(decision.Reservation, new Money(1_400, "INR"), tokens: 10);

        var next = await _allowances.ReserveAsync(owner, new Money(1, "INR"), default);
        Assert.False(next.Allowed);
    }

    // ---------------------------------------------------------- daily ceiling

    [PostgresFact]
    public async Task A_daily_cap_binds_before_the_monthly_one()
    {
        // ₹1,000 for the month but only ₹100 today.
        var owner = Owner(userBudget: 100_000, orgBudget: 1_000_000, dailyBudget: 10_000);

        var first = await _allowances.ReserveAsync(owner, new Money(6_000, "INR"), default);
        var second = await _allowances.ReserveAsync(owner, new Money(6_000, "INR"), default);

        Assert.True(first.Allowed);
        Assert.False(second.Allowed);
        Assert.Equal(AllowanceOutcome.UserDailyExceeded, second.Outcome);
    }

    [PostgresFact]
    public async Task A_request_refused_by_the_day_leaves_the_month_and_the_tenant_untouched()
    {
        var owner = Owner(userBudget: 100_000, orgBudget: 1_000_000, dailyBudget: 1_000);

        var refused = await _allowances.ReserveAsync(owner, new Money(5_000, "INR"), default);
        Assert.False(refused.Allowed);

        var month = await _allowances.UserPeriodAsync(owner, DateTimeOffset.UtcNow, default);
        var org = await _allowances.OrganizationPeriodAsync(owner, DateTimeOffset.UtcNow, default);

        Assert.Equal(0, month.ReservedMinor);
        Assert.Equal(0, month.ConsumedMinor);
        Assert.Equal(0, org.ReservedMinor);
    }

    [PostgresFact]
    public async Task Settling_charges_the_day_the_month_and_the_tenant_alike()
    {
        var owner = Owner(userBudget: 100_000, orgBudget: 1_000_000, dailyBudget: 10_000);

        var decision = await _allowances.ReserveAsync(owner, new Money(5_000, "INR"), default);
        await _allowances.SettleAsync(decision.Reservation, new Money(800, "INR"), tokens: 50);

        var daily = await _allowances.UserDailyPeriodAsync(owner, DateTimeOffset.UtcNow, default);
        var month = await _allowances.UserPeriodAsync(owner, DateTimeOffset.UtcNow, default);

        Assert.Equal(800, daily!.ConsumedMinor);
        Assert.Equal(0, daily.ReservedMinor);
        Assert.Equal(800, month.ConsumedMinor);
        Assert.Equal(0, month.ReservedMinor);
    }

    [PostgresFact]
    public async Task A_released_reservation_frees_the_day_too()
    {
        var owner = Owner(userBudget: 100_000, orgBudget: 1_000_000, dailyBudget: 10_000);

        var decision = await _allowances.ReserveAsync(owner, new Money(9_000, "INR"), default);
        await _allowances.ReleaseAsync(decision.Reservation);

        var daily = await _allowances.UserDailyPeriodAsync(owner, DateTimeOffset.UtcNow, default);
        Assert.Equal(0, daily!.ReservedMinor);
        Assert.True((await _allowances.ReserveAsync(owner, new Money(9_000, "INR"), default)).Allowed);
    }

    [PostgresFact]
    public async Task Tomorrow_is_a_fresh_day_but_the_same_month()
    {
        var owner = Owner(userBudget: 100_000, orgBudget: 1_000_000, dailyBudget: 10_000);
        // Pick a day that is not the last of the month, so "tomorrow" stays in it.
        var today = new DateTimeOffset(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 10, 12, 0, 0, TimeSpan.Zero);

        var todayPeriod = await _allowances.UserDailyPeriodAsync(owner, today, default);
        var tomorrowPeriod = await _allowances.UserDailyPeriodAsync(owner, today.AddDays(1), default);
        var month = await _allowances.UserPeriodAsync(owner, today, default);
        var sameMonth = await _allowances.UserPeriodAsync(owner, today.AddDays(1), default);

        Assert.NotEqual(todayPeriod!.PeriodId, tomorrowPeriod!.PeriodId);
        Assert.Equal(month.PeriodId, sameMonth.PeriodId);
    }

    [PostgresFact]
    public async Task A_daily_period_opened_on_the_first_does_not_collide_with_the_month()
    {
        // Both start at midnight on the 1st; only the distinct scope keeps them apart.
        var owner = Owner(userBudget: 100_000, orgBudget: 1_000_000, dailyBudget: 10_000);
        var firstOfMonth = new DateTimeOffset(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 30, 0, TimeSpan.Zero);

        var month = await _allowances.UserPeriodAsync(owner, firstOfMonth, default);
        var day = await _allowances.UserDailyPeriodAsync(owner, firstOfMonth, default);

        Assert.NotEqual(month.PeriodId, day!.PeriodId);
        Assert.Equal(month.PeriodStart, day.PeriodStart);
    }

    [PostgresFact]
    public async Task No_daily_cap_means_no_daily_period_is_opened()
    {
        var owner = Owner(userBudget: 100_000, orgBudget: 1_000_000);

        var decision = await _allowances.ReserveAsync(owner, new Money(1_000, "INR"), default);

        Assert.True(decision.Allowed);
        Assert.Equal(Guid.Empty, decision.Reservation.UserDailyPeriodId);
        Assert.Null(await _allowances.UserDailyPeriodAsync(owner, DateTimeOffset.UtcNow, default));
    }

    [PostgresFact]
    public async Task Concurrent_requests_cannot_race_past_the_daily_cap_either()
    {
        var owner = Owner(userBudget: 10_000_000, orgBudget: 10_000_000, dailyBudget: 5_000);

        var decisions = await Task.WhenAll(Enumerable.Range(0, 30)
            .Select(_ => _allowances.ReserveAsync(owner, new Money(1_000, "INR"), default)));

        Assert.Equal(5, decisions.Count(d => d.Allowed));
    }

    // ------------------------------------------------------------- concurrency

    [PostgresFact]
    public async Task Fifty_concurrent_requests_cannot_overspend_a_hundred_rupee_budget()
    {
        // The requirement's own scenario. Budget ₹100, each request reserves ₹10:
        // exactly ten may proceed, however they interleave.
        var owner = Owner(userBudget: 10_000, orgBudget: 1_000_000);
        const int concurrent = 50;
        const long each = 1_000;

        var decisions = await Task.WhenAll(Enumerable.Range(0, concurrent)
            .Select(_ => _allowances.ReserveAsync(owner, new Money(each, "INR"), default)));

        var allowed = decisions.Count(d => d.Allowed);
        Assert.Equal(10, allowed);

        var period = await _allowances.UserPeriodAsync(owner, DateTimeOffset.UtcNow, default);
        Assert.Equal(allowed * each, period.ReservedMinor);
        Assert.True(period.AvailableMinor >= 0, "the budget must never be oversold");

        // And every refused request left nothing behind.
        foreach (var refused in decisions.Where(d => !d.Allowed))
            Assert.False(refused.Reservation.Held);
    }

    [PostgresFact]
    public async Task Concurrent_settlements_land_on_the_exact_total()
    {
        var owner = Owner(userBudget: 1_000_000, orgBudget: 10_000_000);
        const int concurrent = 40;

        var decisions = await Task.WhenAll(Enumerable.Range(0, concurrent)
            .Select(_ => _allowances.ReserveAsync(owner, new Money(1_000, "INR"), default)));
        Assert.All(decisions, d => Assert.True(d.Allowed));

        await Task.WhenAll(decisions.Select(d =>
            _allowances.SettleAsync(d.Reservation, new Money(250, "INR"), tokens: 10)));

        var period = await _allowances.UserPeriodAsync(owner, DateTimeOffset.UtcNow, default);
        Assert.Equal(concurrent * 250, period.ConsumedMinor);
        Assert.Equal(0, period.ReservedMinor);
        Assert.Equal(concurrent, period.RequestCount);
    }

    [PostgresFact]
    public async Task The_tenant_pool_is_enforced_across_members()
    {
        // Two members with generous personal allowances still share one tenant budget.
        var secondMembership = Guid.NewGuid();
        var secondUser = Guid.NewGuid();
        await using (var db = _db.NewContext())
        {
            db.Users.Add(new User { Id = secondUser, OrganizationId = OrgId, Email = $"b{Guid.NewGuid():N}@acme.test", PasswordHash = "x" });
            db.Memberships.Add(new Membership { Id = secondMembership, OrganizationId = OrgId, UserId = secondUser, WorkspaceId = WorkspaceId });
            await db.SaveChangesAsync();
        }

        var a = Owner(userBudget: 100_000, orgBudget: 1_500);
        var b = new AllowanceOwner(OrgId, secondMembership, secondUser, WorkspaceId, "INR", 100_000, false, 1_500);

        var first = await _allowances.ReserveAsync(a, new Money(1_000, "INR"), default);
        var second = await _allowances.ReserveAsync(b, new Money(1_000, "INR"), default);

        Assert.True(first.Allowed);
        Assert.False(second.Allowed);
        Assert.Equal(AllowanceOutcome.OrganizationExceeded, second.Outcome);
    }

    // ------------------------------------------------------------- admin edits

    [PostgresFact]
    public async Task Raising_the_allocation_unblocks_the_member_immediately()
    {
        var owner = Owner(userBudget: 500, orgBudget: 100_000);

        var blocked = await _allowances.ReserveAsync(owner, new Money(900, "INR"), default);
        Assert.False(blocked.Allowed);

        var period = await _allowances.UserPeriodAsync(owner, DateTimeOffset.UtcNow, default);
        await _allowances.SetAllocationAsync(period.PeriodId, 5_000, unlimited: false, default);

        var afterRaise = await _allowances.ReserveAsync(Owner(5_000, 100_000), new Money(900, "INR"), default);
        Assert.True(afterRaise.Allowed);
    }

    [PostgresFact]
    public async Task A_top_up_adds_budget_without_touching_consumption()
    {
        var owner = Owner(userBudget: 1_000, orgBudget: 100_000);

        var decision = await _allowances.ReserveAsync(owner, new Money(1_000, "INR"), default);
        await _allowances.SettleAsync(decision.Reservation, new Money(1_000, "INR"), tokens: 1);

        var period = await _allowances.UserPeriodAsync(owner, DateTimeOffset.UtcNow, default);
        var toppedUp = await _allowances.AdjustAsync(period.PeriodId, 2_000, default);

        Assert.Equal(1_000, toppedUp.ConsumedMinor);     // history intact
        Assert.Equal(2_000, toppedUp.AdjustmentMinor);
        Assert.Equal(2_000, toppedUp.AvailableMinor);
    }

    // ----------------------------------------------------------------- periods

    [PostgresFact]
    public async Task Each_month_is_its_own_row_and_older_periods_stay_queryable()
    {
        var owner = Owner(userBudget: 10_000, orgBudget: 100_000);

        var current = await _allowances.UserPeriodAsync(owner, DateTimeOffset.UtcNow, default);
        var nextMonth = await _allowances.UserPeriodAsync(owner, DateTimeOffset.UtcNow.AddMonths(1), default);

        Assert.NotEqual(current.PeriodId, nextMonth.PeriodId);
        Assert.Equal(current.PeriodEnd, nextMonth.PeriodStart);

        await using var db = _db.NewContext();
        var rows = await db.AllowancePeriods.IgnoreQueryFilters()
            .Where(p => p.MembershipId == MembershipId).CountAsync();
        Assert.True(rows >= 2);
    }

    [PostgresFact]
    public async Task Opening_the_same_period_twice_reuses_one_row()
    {
        var owner = Owner(userBudget: 10_000, orgBudget: 100_000);

        var first = await _allowances.UserPeriodAsync(owner, DateTimeOffset.UtcNow, default);
        var again = await _allowances.UserPeriodAsync(owner, DateTimeOffset.UtcNow, default);

        Assert.Equal(first.PeriodId, again.PeriodId);
    }

    [PostgresFact]
    public async Task Concurrent_first_requests_of_a_month_open_exactly_one_period()
    {
        var owner = Owner(userBudget: 10_000, orgBudget: 100_000);
        var at = DateTimeOffset.UtcNow.AddMonths(6);

        var periods = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => _allowances.UserPeriodAsync(owner, at, default)));

        Assert.Single(periods.Select(p => p.PeriodId).Distinct());
    }
}
