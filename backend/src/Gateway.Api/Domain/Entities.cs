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
    /// <summary>
    /// Prepaid token allowance for this (user, workspace). Null = not enforced (the
    /// rolling windows still apply). Debited atomically in TokenBalanceService; every
    /// change is mirrored by a TokenLedgerEntry row.
    /// </summary>
    public long? TokenBalance { get; set; }

    /// <summary>
    /// Tokens credited to TokenBalance once per calendar month (UTC). Null = no
    /// recurring allowance; a one-off balance an admin set still applies.
    /// </summary>
    public long? AllowanceTokens { get; set; }

    /// <summary>true: add to what is left. false: reset the balance to AllowanceTokens.</summary>
    public bool AllowanceRollover { get; set; }

    /// <summary>Last period credited, "yyyy-MM". Null = never credited.</summary>
    public string? AllowancePeriodKey { get; set; }
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

/// <summary>
/// A user's personal key for the Anthropic-compatible proxy (/v1/*). Minted after
/// login by the user (or the setup wizard); only the SHA-256 hash is stored. Carries
/// the same identity as a JWT session so quota/balance/audit attach to the user.
/// </summary>
public sealed class ApiKey
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = "";
    public string KeyHash { get; set; } = "";     // SHA-256 hex of the full key
    public string Prefix { get; set; } = "";      // "apus_ab12cd3" shown in UI
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public bool IsActive => RevokedAt is null && (ExpiresAt is null || ExpiresAt > DateTimeOffset.UtcNow);
}

public enum CredentialKind { ApiKey = 0, OAuth = 1 }

/// <summary>
/// An AI provider credential. OrganizationId null = platform-wide fallback managed by
/// super admins; otherwise it belongs to one organization and is managed by that org's
/// admins. Secrets are AES-256-GCM encrypted at rest (see SecretProtector) and are
/// never returned by any endpoint — only Hint is.
/// </summary>
public sealed class ProviderCredential
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? OrganizationId { get; set; }
    public string Provider { get; set; } = "";            // "anthropic", "openai"
    public CredentialKind Kind { get; set; } = CredentialKind.ApiKey;
    public string EncryptedSecret { get; set; } = "";     // API key, or OAuth access token
    public string? EncryptedRefreshToken { get; set; }    // OAuth only
    public DateTimeOffset? AccessExpiresAt { get; set; }  // OAuth only
    public string? Scopes { get; set; }
    public string Hint { get; set; } = "";                // "...6789" or the account label
    public bool IsActive { get; set; } = true;
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastRefreshedAt { get; set; }
    public string? LastError { get; set; }
}

public enum LedgerKind { Grant = 0, Set = 1, Usage = 2, Revoke = 3, Allowance = 4 }

/// <summary>
/// Immutable history of every token-balance change. Corrections are new rows, never
/// edits. Usage rows reference the request correlation id so a debit can be traced
/// back to the ClickHouse usage event.
/// </summary>
public sealed class TokenLedgerEntry
{
    public long Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid MembershipId { get; set; }
    public Guid UserId { get; set; }
    public Guid WorkspaceId { get; set; }
    public LedgerKind Kind { get; set; }
    public long Delta { get; set; }          // signed; negative for usage/revoke
    public long? BalanceAfter { get; set; }  // null when the balance became unenforced
    public Guid? ActorUserId { get; set; }   // admin who granted/set/revoked; null for usage
    public string? Reference { get; set; }   // note, or correlation id for usage rows
    public string? IdempotencyKey { get; set; }
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
