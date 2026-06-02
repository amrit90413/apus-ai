namespace Gateway.Api.Domain;

// Organization → Workspace → User → Session → AI Requests → Usage Logs
// Every tenant-owned row carries OrganizationId so a global query filter can enforce
// isolation at the EF level (see GatewayDbContext.OnModelCreating).

public sealed class Organization
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string PlanCode { get; set; } = "free"; // FK to Plan
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<Workspace> Workspaces { get; set; } = new();
}

public sealed class Workspace
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = "";
    // JSON-serialized List<QuotaWindow> + allowed models; overrides org/plan defaults.
    public string? QuotaPolicyJson { get; set; }
    public bool IsActive { get; set; } = true;
    public List<Membership> Memberships { get; set; } = new();
}

public enum Role { User = 0, WorkspaceAdmin = 1, OrgAdmin = 2, SuperAdmin = 3 }

public sealed class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = ""; // Argon2id
    public string? PhoneNumber { get; set; }        // E.164 format e.g. "919876543210"
    public bool PhoneVerified { get; set; } = false;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<Membership> Memberships { get; set; } = new();
}

/// <summary>A user's role within a specific workspace (RBAC join row).</summary>
public sealed class Membership
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Role Role { get; set; } = Role.User;
    public string? PerUserQuotaJson { get; set; } // optional per-user override
}

/// <summary>A CLI/device session bound to a refresh token + device fingerprint.</summary>
public sealed class Session
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
    public Guid WorkspaceId { get; set; }
    public string DeviceName { get; set; } = "";
    public string DeviceFingerprint { get; set; } = "";
    public string RefreshTokenHash { get; set; } = "";
    public string? LastIp { get; set; }                 // anomaly detection only
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public bool IsActive => RevokedAt is null && ExpiresAt > DateTimeOffset.UtcNow;
}

/// <summary>Platform-level AI provider API keys. Managed by super admins via the dashboard.</summary>
public sealed class ProviderKey
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Provider { get; set; } = "";        // "anthropic", "openai"
    public string EncryptedKey { get; set; } = "";    // AES-256-GCM, base64
    public string KeyHint { get; set; } = "";          // "...6789" shown in UI
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AuditLog
{
    public long Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid? UserId { get; set; }
    public string Action { get; set; } = "";    // login, logout, quota_blocked, model_denied, anomaly...
    public string? Detail { get; set; }
    public string? Ip { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}
