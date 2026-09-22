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
    public DbSet<ProviderCredential> ProviderCredentials => Set<ProviderCredential>();
    public DbSet<TokenLedgerEntry> TokenLedger => Set<TokenLedgerEntry>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

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
            e.Property(x => x.TokenBalance).HasColumnName("token_balance");
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

        b.Entity<ProviderCredential>(e =>
        {
            e.ToTable("provider_credentials");
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.Provider).HasColumnName("provider");
            e.Property(x => x.Kind).HasColumnName("kind").HasConversion<int>();
            e.Property(x => x.EncryptedSecret).HasColumnName("encrypted_secret");
            e.Property(x => x.EncryptedRefreshToken).HasColumnName("encrypted_refresh_token");
            e.Property(x => x.AccessExpiresAt).HasColumnName("access_expires_at");
            e.Property(x => x.Scopes).HasColumnName("scopes");
            e.Property(x => x.Hint).HasColumnName("hint");
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.CreatedBy).HasColumnName("created_by");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.LastRefreshedAt).HasColumnName("last_refreshed_at");
            e.Property(x => x.LastError).HasColumnName("last_error");
            e.HasIndex(x => new { x.OrganizationId, x.Provider, x.IsActive });
        });

        b.Entity<ApiKey>(e =>
        {
            e.ToTable("api_keys");
            e.Ignore(x => x.IsActive);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.WorkspaceId).HasColumnName("workspace_id");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.KeyHash).HasColumnName("key_hash");
            e.Property(x => x.Prefix).HasColumnName("prefix");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.LastUsedAt).HasColumnName("last_used_at");
            e.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            e.Property(x => x.RevokedAt).HasColumnName("revoked_at");
            e.HasIndex(x => x.KeyHash).IsUnique();
            e.HasIndex(x => x.UserId);
        });

        b.Entity<TokenLedgerEntry>(e =>
        {
            e.ToTable("token_ledger");
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.MembershipId).HasColumnName("membership_id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.WorkspaceId).HasColumnName("workspace_id");
            e.Property(x => x.Kind).HasColumnName("kind").HasConversion<int>();
            e.Property(x => x.Delta).HasColumnName("delta");
            e.Property(x => x.BalanceAfter).HasColumnName("balance_after");
            e.Property(x => x.ActorUserId).HasColumnName("actor_user_id");
            e.Property(x => x.Reference).HasColumnName("reference");
            e.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasIndex(x => new { x.MembershipId, x.CreatedAt });
            e.HasIndex(x => new { x.OrganizationId, x.IdempotencyKey }).IsUnique();
        });

        // Tenant isolation — SuperAdmin bypasses by setting CurrentOrganizationId = null.
        b.Entity<Workspace>().HasQueryFilter(w => _tenant.CurrentOrganizationId == null || w.OrganizationId == _tenant.CurrentOrganizationId);
        b.Entity<User>().HasQueryFilter(u => _tenant.CurrentOrganizationId == null || u.OrganizationId == _tenant.CurrentOrganizationId);
        b.Entity<Membership>().HasQueryFilter(m => _tenant.CurrentOrganizationId == null || m.OrganizationId == _tenant.CurrentOrganizationId);
        b.Entity<Session>().HasQueryFilter(s => _tenant.CurrentOrganizationId == null || s.OrganizationId == _tenant.CurrentOrganizationId);
        b.Entity<AuditLog>().HasQueryFilter(a => _tenant.CurrentOrganizationId == null || a.OrganizationId == _tenant.CurrentOrganizationId);
        b.Entity<TokenLedgerEntry>().HasQueryFilter(t => _tenant.CurrentOrganizationId == null || t.OrganizationId == _tenant.CurrentOrganizationId);
        b.Entity<ApiKey>().HasQueryFilter(k => _tenant.CurrentOrganizationId == null || k.OrganizationId == _tenant.CurrentOrganizationId);
        // Platform-wide credentials (OrganizationId null) are visible to every tenant as a
        // fallback; org-owned rows are scoped like everything else.
        b.Entity<ProviderCredential>().HasQueryFilter(c => _tenant.CurrentOrganizationId == null || c.OrganizationId == null || c.OrganizationId == _tenant.CurrentOrganizationId);
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
