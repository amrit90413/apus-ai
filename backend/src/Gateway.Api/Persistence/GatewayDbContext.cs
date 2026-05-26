using Gateway.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Persistence;

/// <summary>
/// PostgreSQL context. Every tenant-owned entity has a global query filter on
/// OrganizationId, so a missing WHERE clause can never leak another tenant's data.
/// The current org id is supplied per-request from the JWT via ITenantContext.
/// </summary>
public sealed class GatewayDbContext : DbContext
{
    private readonly ITenantContext _tenant;

    public GatewayDbContext(DbContextOptions<GatewayDbContext> options, ITenantContext tenant)
        : base(options)
    {
        _tenant = tenant;
    }

    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Organization>().HasIndex(o => o.Slug).IsUnique();
        b.Entity<User>().HasIndex(u => new { u.OrganizationId, u.Email }).IsUnique();
        b.Entity<Membership>().HasIndex(m => new { m.UserId, m.WorkspaceId }).IsUnique();
        b.Entity<Session>().HasIndex(s => s.RefreshTokenHash);

        // Tenant isolation — SuperAdmin bypasses by setting CurrentOrganizationId = null.
        b.Entity<Workspace>().HasQueryFilter(w => _tenant.CurrentOrganizationId == null || w.OrganizationId == _tenant.CurrentOrganizationId);
        b.Entity<User>().HasQueryFilter(u => _tenant.CurrentOrganizationId == null || u.OrganizationId == _tenant.CurrentOrganizationId);
        b.Entity<Membership>().HasQueryFilter(m => _tenant.CurrentOrganizationId == null || m.OrganizationId == _tenant.CurrentOrganizationId);
        b.Entity<Session>().HasQueryFilter(s => _tenant.CurrentOrganizationId == null || s.OrganizationId == _tenant.CurrentOrganizationId);
        b.Entity<AuditLog>().HasQueryFilter(a => _tenant.CurrentOrganizationId == null || a.OrganizationId == _tenant.CurrentOrganizationId);
    }
}

/// <summary>Per-request tenant scope, populated from the JWT in middleware.</summary>
public interface ITenantContext
{
    Guid? CurrentOrganizationId { get; set; } // null = super-admin / cross-tenant
}

public sealed class TenantContext : ITenantContext
{
    public Guid? CurrentOrganizationId { get; set; }
}
