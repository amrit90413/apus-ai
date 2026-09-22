using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gateway.Api.Domain;
using Gateway.Api.Persistence;

namespace Gateway.Api.Security;

/// <summary>Resource kinds that appear in the audit trail.</summary>
public static class AuditResources
{
    public const string ProviderConnection = "provider_connection";
    public const string Membership = "membership";
    public const string Allowance = "allowance";
    public const string Organization = "organization";
    public const string User = "user";
    public const string ModelAccess = "model_access";
    public const string Subscription = "subscription";
}

public static class AuditActions
{
    public const string ProviderConnected = "provider_connected";
    public const string ProviderDisconnected = "provider_disconnected";
    public const string ProviderTokenRefreshed = "provider_token_refreshed";
    public const string ProviderReconnectRequired = "provider_reconnect_required";
    public const string ProviderValidated = "provider_validated";
    public const string ProviderEnabled = "provider_enabled";
    public const string ProviderDisabled = "provider_disabled";
    public const string CredentialRotated = "credential_rotated";
    public const string OAuthStarted = "provider_oauth_started";
    public const string OAuthFailed = "provider_oauth_failed";

    public const string AllowanceChanged = "allowance_changed";
    public const string AllowanceReset = "allowance_reset";
    public const string AllowanceTopUp = "allowance_top_up";

    public const string MemberInvited = "member_invited";
    public const string MemberDisabled = "member_disabled";
    public const string MemberSuspended = "member_suspended";
    public const string MemberReactivated = "member_reactivated";
    public const string MemberDeleted = "member_deleted";
    public const string RoleChanged = "role_changed";

    public const string ModelAccessChanged = "model_access_changed";
    public const string ProviderAccessChanged = "provider_access_changed";
    public const string OrgSettingsChanged = "org_settings_changed";
}

/// <summary>
/// Writes the immutable audit trail. Every record carries who did it, to what, from
/// where, and the before/after state.
///
/// Before/after payloads are redacted on the way in: any field whose name looks like
/// a secret is replaced with a marker, so a careless caller cannot put a provider key
/// into the audit log.
/// </summary>
public interface IAuditWriter
{
    /// <summary>Queues a record on the current DbContext; the caller's SaveChanges commits it with their change.</summary>
    void Record(string action, string resourceType, string? resourceId, object? before = null, object? after = null, string? detail = null);

    /// <summary>Queues and saves immediately. Use when there is no surrounding transaction to join.</summary>
    Task WriteAsync(string action, string resourceType, string? resourceId, object? before = null, object? after = null, string? detail = null, CancellationToken ct = default);

    /// <summary>For background workers and callbacks that have no authenticated principal.</summary>
    Task WriteSystemAsync(Guid organizationId, string action, string resourceType, string? resourceId, string? detail = null, CancellationToken ct = default);
}

public sealed class AuditWriter : IAuditWriter
{
    private static readonly string[] SecretFieldMarkers =
    {
        "secret", "password", "token", "apikey", "api_key", "key", "credential",
        "authorization", "privatekey", "private_key", "clientsecret", "assertion",
    };
    private const string Redacted = "[redacted]";

    private readonly GatewayDbContext _db;
    private readonly IHttpContextAccessor _http;

    public AuditWriter(GatewayDbContext db, IHttpContextAccessor http)
    {
        _db = db; _http = http;
    }

    public void Record(string action, string resourceType, string? resourceId, object? before = null, object? after = null, string? detail = null)
    {
        var ctx = _http.HttpContext;
        var user = ctx?.User;
        var orgId = ParseGuid(user?.FindFirstValue("org_id"));
        if (orgId is null) return; // nothing to attribute the record to

        _db.AuditLogs.Add(new AuditLog
        {
            OrganizationId = orgId.Value,
            UserId = ParseGuid(user?.FindFirstValue(ClaimTypes.NameIdentifier)),
            ActorEmail = user?.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Email),
            Action = action,
            ResourceType = resourceType,
            ResourceId = resourceId,
            Detail = Truncate(detail, 1000),
            Ip = ctx?.Connection.RemoteIpAddress?.ToString(),
            UserAgent = Truncate(ctx?.Request.Headers.UserAgent.ToString(), 400),
            CorrelationId = ctx?.TraceIdentifier,
            BeforeJson = Redact(before),
            AfterJson = Redact(after),
        });
    }

    public async Task WriteAsync(string action, string resourceType, string? resourceId, object? before = null, object? after = null, string? detail = null, CancellationToken ct = default)
    {
        Record(action, resourceType, resourceId, before, after, detail);
        await _db.SaveChangesAsync(ct);
    }

    public async Task WriteSystemAsync(Guid organizationId, string action, string resourceType, string? resourceId, string? detail = null, CancellationToken ct = default)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            OrganizationId = organizationId,
            Action = action,
            ResourceType = resourceType,
            ResourceId = resourceId,
            Detail = Truncate(detail, 1000),
            ActorEmail = "system",
        });
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Serializes an object with every secret-looking field replaced.</summary>
    internal static string? Redact(object? value)
    {
        if (value is null) return null;
        var node = value as JsonNode ?? JsonSerializer.SerializeToNode(value);
        if (node is null) return null;
        Scrub(node);
        return node.ToJsonString();
    }

    private static void Scrub(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    if (LooksSecret(key)) obj[key] = Redacted;
                    else if (obj[key] is { } child) Scrub(child);
                }
                break;
            case JsonArray arr:
                foreach (var item in arr)
                    if (item is not null) Scrub(item);
                break;
        }
    }

    private static bool LooksSecret(string name)
    {
        var lower = name.ToLowerInvariant();
        return SecretFieldMarkers.Any(m => lower.Contains(m, StringComparison.Ordinal));
    }

    private static Guid? ParseGuid(string? value) => Guid.TryParse(value, out var g) ? g : null;

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? null : value.Length <= max ? value : value[..max];
}
