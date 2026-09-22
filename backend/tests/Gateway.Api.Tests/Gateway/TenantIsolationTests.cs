using Gateway.Api.Domain;
using Gateway.Api.Persistence;
using Gateway.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Tests.Gateway;

/// <summary>
/// Cross-tenant access attempts, made directly against the data layer.
///
/// These deliberately bypass the controllers: the guarantee being tested is that
/// isolation is enforced at the repository boundary, so a controller that forgets a
/// WHERE clause still cannot leak another tenant's data.
/// </summary>
public sealed class TenantIsolationTests : IAsyncLifetime
{
    private readonly PostgresDatabase _db = new();

    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();
    private static readonly Guid WorkspaceA = Guid.NewGuid();
    private static readonly Guid WorkspaceB = Guid.NewGuid();
    private static readonly Guid UserA = Guid.NewGuid();
    private static readonly Guid UserB = Guid.NewGuid();
    private static readonly Guid MembershipA = Guid.NewGuid();
    private static readonly Guid MembershipB = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        if (!TestInfra.HasPostgres) return;

        await using var db = _db.NewContext();
        // Two passes: the dependent rows carry no EF-declared relationship to
        // organizations, so EF cannot infer the insert order on its own.
        SeedTenant(db, OrgA, WorkspaceA, UserA, MembershipA, "a");
        SeedTenant(db, OrgB, WorkspaceB, UserB, MembershipB, "b");
        await db.SaveChangesAsync();

        SeedActivity(db, OrgA, WorkspaceA, UserA, MembershipA, "a");
        SeedActivity(db, OrgB, WorkspaceB, UserB, MembershipB, "b");
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private static void SeedTenant(GatewayDbContext db, Guid org, Guid workspace, Guid user, Guid membership, string tag)
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        db.Organizations.Add(new Organization { Id = org, Name = tag, Slug = $"{tag}-{suffix}" });
        db.Workspaces.Add(new Workspace { Id = workspace, OrganizationId = org, Name = tag });
        db.Users.Add(new User { Id = user, OrganizationId = org, Email = $"{tag}-{suffix}@test", PasswordHash = "x" });
        db.Memberships.Add(new Membership { Id = membership, OrganizationId = org, UserId = user, WorkspaceId = workspace, MonthlyAllowanceMinor = 10_000 });
    }

    private static void SeedActivity(GatewayDbContext db, Guid org, Guid workspace, Guid user, Guid membership, string tag)
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        db.ApiKeys.Add(new ApiKey { OrganizationId = org, UserId = user, WorkspaceId = workspace, Name = tag, KeyHash = $"hash-{suffix}", Prefix = $"apus_{tag}" });
        db.UsageLedger.Add(new AiUsageLedgerEntry
        {
            RequestId = $"req-{suffix}", OrganizationId = org, WorkspaceId = workspace, UserId = user,
            Provider = "anthropic", Model = "claude-sonnet-5", Currency = "INR",
            CustomerCostMinor = 500, ProviderCostMinor = 400,
            StartedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow,
            Status = UsageStatus.Succeeded, BillingPeriod = DateOnly.FromDateTime(DateTime.UtcNow),
        });
        db.AllowancePeriods.Add(new AllowancePeriod
        {
            OrganizationId = org, Scope = AllowanceScope.User, MembershipId = membership,
            UserId = user, WorkspaceId = workspace, Currency = "INR",
            PeriodStart = new DateTimeOffset(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, TimeSpan.Zero),
            PeriodEnd = new DateTimeOffset(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1),
            AllocatedMinor = 10_000,
        });
        db.AuditLogs.Add(new AuditLog { OrganizationId = org, UserId = user, Action = "seed" });
        db.Notifications.Add(new NotificationOutboxEntry { OrganizationId = org, Kind = "seed", Subject = tag, Body = tag });
    }

    private GatewayDbContext AsTenant(Guid org) => _db.NewContext(new TenantContext { CurrentOrganizationId = org });

    [PostgresFact]
    public async Task A_tenant_sees_only_its_own_users_and_memberships()
    {
        await using var db = AsTenant(OrgA);

        Assert.Single(await db.Users.ToListAsync());
        Assert.Single(await db.Memberships.ToListAsync());
        Assert.Null(await db.Users.FirstOrDefaultAsync(u => u.Id == UserB));
        Assert.Null(await db.Memberships.FirstOrDefaultAsync(m => m.Id == MembershipB));
    }

    [PostgresFact]
    public async Task A_tenant_cannot_read_another_tenants_usage_ledger()
    {
        await using var db = AsTenant(OrgA);

        var rows = await db.UsageLedger.ToListAsync();

        Assert.Single(rows);
        Assert.Equal(OrgA, rows[0].OrganizationId);
    }

    [PostgresFact]
    public async Task A_tenant_cannot_read_another_tenants_allowance_periods()
    {
        await using var db = AsTenant(OrgA);

        var periods = await db.AllowancePeriods.ToListAsync();

        Assert.Single(periods);
        Assert.Equal(MembershipA, periods[0].MembershipId);
    }

    [PostgresFact]
    public async Task A_tenant_cannot_change_another_tenants_quota()
    {
        await using var db = AsTenant(OrgA);

        // The classic attack: a valid id from another tenant supplied to an admin route.
        var target = await db.Memberships.FirstOrDefaultAsync(m => m.Id == MembershipB);

        Assert.Null(target);   // there is nothing to mutate

        await using var verify = _db.NewContext();
        var untouched = await verify.Memberships.IgnoreQueryFilters().FirstAsync(m => m.Id == MembershipB);
        Assert.Equal(10_000, untouched.MonthlyAllowanceMinor);
    }

    [PostgresFact]
    public async Task A_tenant_cannot_read_another_tenants_api_keys_audit_or_notifications()
    {
        await using var db = AsTenant(OrgA);

        Assert.Single(await db.ApiKeys.ToListAsync());
        Assert.Single(await db.AuditLogs.ToListAsync());
        Assert.Single(await db.Notifications.ToListAsync());
    }

    [PostgresFact]
    public async Task A_tenant_cannot_add_a_member_to_another_tenants_workspace()
    {
        await using var db = AsTenant(OrgA);

        var foreignWorkspace = await db.Workspaces.FirstOrDefaultAsync(w => w.Id == WorkspaceB);

        Assert.Null(foreignWorkspace);
    }

    [PostgresFact]
    public async Task A_platform_admin_scope_sees_every_tenant()
    {
        // CurrentOrganizationId == null is the super-admin scope.
        await using var db = _db.NewContext(new TenantContext { CurrentOrganizationId = null });

        Assert.Equal(2, await db.Users.CountAsync());
        Assert.Equal(2, await db.UsageLedger.CountAsync());
        Assert.Equal(2, await db.AllowancePeriods.CountAsync());
    }

    [PostgresFact]
    public async Task Aggregates_are_scoped_too_so_totals_cannot_leak_across_tenants()
    {
        await using var db = AsTenant(OrgA);

        var total = await db.UsageLedger.SumAsync(u => u.CustomerCostMinor);

        Assert.Equal(500, total);   // not 1000
    }
}
