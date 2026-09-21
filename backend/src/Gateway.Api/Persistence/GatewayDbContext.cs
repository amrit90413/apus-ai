using Gateway.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Persistence;

/// <summary>
/// PostgreSQL context. Every tenant-owned entity has a global query filter on
/// OrganizationId, so a missing WHERE clause can never leak another tenant's data.
/// The current org id is supplied per-request from the JWT via ITenantContext.
///
/// Table and column names are mapped explicitly to the snake_case schema in
/// infra/db/postgres-init.sql. Without this, EF's default convention emits quoted
/// PascalCase identifiers ("Users"), which Postgres treats as case-sensitive and
/// distinct from the lowercase tables the init script creates.
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
    public DbSet<ProviderKey> ProviderKeys => Set<ProviderKey>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Organization>(e =>
        {
            e.ToTable("organizations");
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.Slug).HasColumnName("slug");
            e.Property(x => x.PlanCode).HasColumnName("plan_code");
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasIndex(x => x.Slug).IsUnique();
        });

        b.Entity<Workspace>(e =>
        {
            e.ToTable("workspaces");
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.QuotaPolicyJson).HasColumnName("quota_policy_json").HasColumnType("jsonb");
            e.Property(x => x.IsActive).HasColumnName("is_active");
        });

        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.Email).HasColumnName("email");
            e.Property(x => x.PasswordHash).HasColumnName("password_hash");
            e.Property(x => x.PhoneNumber).HasColumnName("phone_number");
            e.Property(x => x.PhoneVerified).HasColumnName("phone_verified");
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasIndex(x => new { x.OrganizationId, x.Email }).IsUnique();
        });

        b.Entity<Membership>(e =>
        {
            e.ToTable("memberships");
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.WorkspaceId).HasColumnName("workspace_id");
            e.Property(x => x.Role).HasColumnName("role").HasConversion<int>();
            e.Property(x => x.PerUserQuotaJson).HasColumnName("per_user_quota_json").HasColumnType("jsonb");
            e.HasIndex(x => new { x.UserId, x.WorkspaceId }).IsUnique();
        });

        b.Entity<Session>(e =>
        {
            e.ToTable("sessions");
            e.Ignore(x => x.IsActive); // computed in code, not a column
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.WorkspaceId).HasColumnName("workspace_id");
            e.Property(x => x.DeviceName).HasColumnName("device_name");
            e.Property(x => x.DeviceFingerprint).HasColumnName("device_fingerprint");
            e.Property(x => x.RefreshTokenHash).HasColumnName("refresh_token_hash");
            e.Property(x => x.LastIp).HasColumnName("last_ip");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.RevokedAt).HasColumnName("revoked_at");
            e.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            e.HasIndex(x => x.RefreshTokenHash);
        });

        b.Entity<AuditLog>(e =>
        {
            e.ToTable("audit_logs");
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.Action).HasColumnName("action");
            e.Property(x => x.Detail).HasColumnName("detail");
            e.Property(x => x.Ip).HasColumnName("ip");
            e.Property(x => x.At).HasColumnName("at");
            e.HasIndex(x => new { x.OrganizationId, x.At });
        });

        b.Entity<ProviderKey>(e =>
        {
            e.ToTable("provider_keys");
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.Provider).HasColumnName("provider");
            e.Property(x => x.EncryptedKey).HasColumnName("encrypted_key");
            e.Property(x => x.KeyHint).HasColumnName("key_hint");
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
        });

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
