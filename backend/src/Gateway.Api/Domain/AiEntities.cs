namespace Gateway.Api.Domain;

// Entities introduced by the multi-provider gateway (migration 003). The tenant
// entities they hang off — Organization, Workspace, User, Membership — live in
// Entities.cs and are extended there rather than duplicated here.

/// <summary>How a tenant authenticates to a provider. Drives which fields of a connection are used.</summary>
public enum ConnectionType
{
    ApiKey = 0,
    OAuth = 1,
    AwsBedrock = 2,
    GoogleVertex = 3,
}

/// <summary>
/// Connection health as an explicit state machine rather than a boolean, so
/// "expired but refreshable" and "revoked by the provider" are distinguishable and
/// the UI can tell an admin what to actually do about it.
/// </summary>
public enum ConnectionStatus
{
    Disconnected = 0,
    Connecting = 1,
    Connected = 2,
    Refreshing = 3,
    Expired = 4,
    ReauthenticationRequired = 5,
    Revoked = 6,
    Disabled = 7,
    Error = 8,
}

public static class ConnectionStatuses
{
    /// <summary>States that still occupy the one-live-connection slot for (tenant, provider, purpose).</summary>
    public static readonly ConnectionStatus[] Live =
    {
        ConnectionStatus.Connecting, ConnectionStatus.Connected, ConnectionStatus.Refreshing,
        ConnectionStatus.Expired, ConnectionStatus.ReauthenticationRequired, ConnectionStatus.Error,
    };

    public static bool IsLive(ConnectionStatus s) => Array.IndexOf(Live, s) >= 0;

    /// <summary>States the gateway will attempt an upstream call with.</summary>
    public static bool CanServe(ConnectionStatus s) =>
        s is ConnectionStatus.Connected or ConnectionStatus.Refreshing or ConnectionStatus.Expired or ConnectionStatus.Error;

    public static string Wire(ConnectionStatus s) => s switch
    {
        ConnectionStatus.Disconnected => "disconnected",
        ConnectionStatus.Connecting => "connecting",
        ConnectionStatus.Connected => "connected",
        ConnectionStatus.Refreshing => "refreshing",
        ConnectionStatus.Expired => "expired",
        ConnectionStatus.ReauthenticationRequired => "reauthentication_required",
        ConnectionStatus.Revoked => "revoked",
        ConnectionStatus.Disabled => "disabled",
        _ => "error",
    };
}

/// <summary>
/// Who a period belongs to. UserDaily is its own scope rather than a User row with a
/// day-long window: on the 1st of a month the two would share a period_start and
/// collide on the uniqueness index.
/// </summary>
public enum AllowanceScope { Organization = 0, User = 1, UserDaily = 2 }

/// <summary>
/// One owner's budget for one calendar period. A "monthly reset" opens the next
/// period; it never zeroes a counter, so history stays queryable and an in-flight
/// request can't be silently re-credited.
///
/// Available = Allocated + Adjustment - Consumed - Reserved. All amounts are minor
/// units (paise, cents) of <see cref="Currency"/> — never floating point.
/// </summary>
public sealed class AllowancePeriod
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public AllowanceScope Scope { get; set; }
    public Guid? MembershipId { get; set; }
    public Guid? UserId { get; set; }
    public Guid? WorkspaceId { get; set; }
    public string Currency { get; set; } = "USD";
    public DateTimeOffset PeriodStart { get; set; }
    public DateTimeOffset PeriodEnd { get; set; }
    public long AllocatedMinor { get; set; }
    public bool Unlimited { get; set; }
    public long ConsumedMinor { get; set; }
    public long ReservedMinor { get; set; }
    public long AdjustmentMinor { get; set; }
    public long RequestCount { get; set; }
    public long TokenCount { get; set; }
    /// <summary>Bitmask of thresholds already notified: 1=50%, 2=75%, 4=90%, 8=100%.</summary>
    public int NotifiedThresholds { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public long AvailableMinor => Unlimited
        ? long.MaxValue
        : AllocatedMinor + AdjustmentMinor - ConsumedMinor - ReservedMinor;
}

public enum UsageStatus { Succeeded = 0, Failed = 1, Blocked = 2, Cancelled = 3, Adjustment = 4 }

/// <summary>
/// Immutable accounting row for one provider request. Provider cost and customer
/// cost are tracked separately so margin, spend and reconciliation stay answerable.
/// Never updated — a correction is a new row with <see cref="UsageStatus.Adjustment"/>.
/// </summary>
public sealed class AiUsageLedgerEntry
{
    public long Id { get; set; }
    public string RequestId { get; set; } = "";
    public Guid OrganizationId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid UserId { get; set; }
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public Guid? ProviderConnectionId { get; set; }

    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CachedInputTokens { get; set; }
    public int CacheWriteTokens { get; set; }

    public long ProviderCostMinor { get; set; }
    public long CustomerCostMinor { get; set; }
    public string Currency { get; set; } = "USD";
    public Guid? PricingId { get; set; }

    public int LatencyMs { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public UsageStatus Status { get; set; }
    public int? HttpStatus { get; set; }
    public string? FailureCategory { get; set; }
    /// <summary>Set when a fallback served this request; names the provider originally routed to.</summary>
    public string? FallbackFrom { get; set; }
    public DateOnly BillingPeriod { get; set; }
    public string? MetadataJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A price list version. Costs are reproducible because every ledger row records the
/// pricing row id used, so a later price change cannot rewrite history.
/// </summary>
public sealed class ProviderModelPricing
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public string Currency { get; set; } = "USD";
    public decimal InputPerMTok { get; set; }
    public decimal OutputPerMTok { get; set; }
    public decimal CachedInputPerMTok { get; set; }
    public decimal CacheWritePerMTok { get; set; }
    public DateTimeOffset EffectiveFrom { get; set; }
    public DateTimeOffset? EffectiveTo { get; set; }
    public string? Source { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Queued notification. Written inside the transaction that observed the condition
/// so a crash cannot lose it, and delivered by NotificationWorker.
/// </summary>
public sealed class NotificationOutboxEntry
{
    public long Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid? UserId { get; set; }
    public string Kind { get; set; } = "";
    public string Severity { get; set; } = "info";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public string? PayloadJson { get; set; }
    /// <summary>Unique per organization — the insert is the "already notified?" check.</summary>
    public string? DedupeKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeliveredAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}

/// <summary>AI access state for one member, independent of whether they can log in at all.</summary>
public enum AiAccessStatus { Active = 0, Suspended = 1, Disabled = 2 }
