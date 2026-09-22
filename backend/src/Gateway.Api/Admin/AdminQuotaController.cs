using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gateway.Api.Domain;
using Gateway.Api.Gateway;
using Gateway.Api.Persistence;
using Gateway.Api.Providers;
using Gateway.Api.Quota;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Admin;

public sealed record SetUserQuotaRequest(QuotaWindow[]? Windows, string[]? AllowedModels);

public sealed record SetWorkspacePolicyRequest(
    string[]? AllowedModels,
    QuotaWindow[]? UserWindows,
    QuotaWindow[]? WorkspaceWindows,
    int? RequestsPerMinute);

/// <summary>
/// Read/write the fair-usage policy. Membership.PerUserQuotaJson and
/// Workspace.QuotaPolicyJson were previously readable by QuotaPolicyResolver but
/// had no write path, so every user fell through to the hardcoded defaults.
///
/// Writes evict this pod's policy cache immediately; other replicas pick the change
/// up when their 30s entry expires.
/// </summary>
[ApiController]
[Route("api/v1/admin")]
[Authorize(Policy = "OrgAdmin")]
public sealed class AdminQuotaController : ControllerBase
{
    private const int MaxWindows = 8;
    private const int MaxModels = 32;
    private const int MaxWindowMinutes = 527_040; // 366 days
    private static readonly Regex WindowName = new("^[A-Za-z0-9_-]{1,32}$", RegexOptions.Compiled);

    private readonly GatewayDbContext _db;
    private readonly IQuotaPolicyResolver _policies;
    private readonly IPolicyCache _cache;
    private readonly ProviderRouter _router;

    public AdminQuotaController(
        GatewayDbContext db, IQuotaPolicyResolver policies, IPolicyCache cache, ProviderRouter router)
    {
        _db = db; _policies = policies; _cache = cache; _router = router;
    }

    private Guid OrgId => Guid.Parse(User.FindFirstValue("org_id")!);
    private Guid ActorId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // ---------------------------------------------------------------- per user

    // GET /api/v1/admin/users/{id}/quota[?workspaceId=...]
    [HttpGet("users/{id:guid}/quota")]
    public async Task<IActionResult> GetUserQuota(Guid id, [FromQuery] Guid? workspaceId, CancellationToken ct)
    {
        var (membership, error) = await ResolveMembership(id, workspaceId, ct);
        if (error is not null) return error;

        var effective = await _policies.ResolveAsync(
            new QuotaPrincipal(id, membership!.WorkspaceId), ct);

        return Ok(new
        {
            userId = id,
            workspaceId = membership.WorkspaceId,
            hasOverride = !string.IsNullOrWhiteSpace(membership.PerUserQuotaJson),
            @override = QuotaPolicyResolver.DeserializeOverride(membership.PerUserQuotaJson),
            effective = new
            {
                effective.AllowedModels,
                effective.UserWindows,
                effective.WorkspaceWindows,
                effective.RequestsPerMinute
            }
        });
    }

    // PUT /api/v1/admin/users/{id}/quota[?workspaceId=...]
    [HttpPut("users/{id:guid}/quota")]
    public async Task<IActionResult> SetUserQuota(
        Guid id, [FromQuery] Guid? workspaceId, [FromBody] SetUserQuotaRequest req, CancellationToken ct)
    {
        var (membership, error) = await ResolveMembership(id, workspaceId, ct);
        if (error is not null) return error;

        if (req.Windows is null && req.AllowedModels is null)
            return Invalid("Provide windows, allowedModels, or both.", "empty_override");

        if (Validate(req.Windows, req.AllowedModels) is { } invalid) return invalid;

        var payload = new UserOverride(req.Windows, req.AllowedModels);
        membership!.PerUserQuotaJson = JsonSerializer.Serialize(payload, QuotaPolicyResolver.JsonOptions);

        _db.AuditLogs.Add(new AuditLog
        {
            OrganizationId = OrgId,
            UserId = ActorId,
            Action = "user_quota_set",
            Detail = $"targetUser={id} workspace={membership.WorkspaceId} " +
                     $"windows={req.Windows?.Length ?? 0} models={req.AllowedModels?.Length ?? 0}"
        });

        await _db.SaveChangesAsync(ct);
        _cache.InvalidateUser(id, membership.WorkspaceId);

        return Ok(new { userId = id, workspaceId = membership.WorkspaceId, @override = payload });
    }

    // DELETE /api/v1/admin/users/{id}/quota[?workspaceId=...]
    [HttpDelete("users/{id:guid}/quota")]
    public async Task<IActionResult> ClearUserQuota(Guid id, [FromQuery] Guid? workspaceId, CancellationToken ct)
    {
        var (membership, error) = await ResolveMembership(id, workspaceId, ct);
        if (error is not null) return error;

        membership!.PerUserQuotaJson = null;

        _db.AuditLogs.Add(new AuditLog
        {
            OrganizationId = OrgId,
            UserId = ActorId,
            Action = "user_quota_cleared",
            Detail = $"targetUser={id} workspace={membership.WorkspaceId}"
        });

        await _db.SaveChangesAsync(ct);
        _cache.InvalidateUser(id, membership.WorkspaceId);

        return Ok(new { userId = id, workspaceId = membership.WorkspaceId, inheritsWorkspacePolicy = true });
    }

    // ----------------------------------------------------------- per workspace

    // GET /api/v1/admin/workspaces/{id}/policy
    [HttpGet("workspaces/{id:guid}/policy")]
    public async Task<IActionResult> GetWorkspacePolicy(Guid id, CancellationToken ct)
    {
        var ws = await _db.Workspaces.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (ws is null) return NotFound(new { error = new { code = "workspace_not_found", message = "Workspace not found." } });

        var stored = QuotaPolicyResolver.DeserializePolicy(ws.QuotaPolicyJson);
        return Ok(new
        {
            workspaceId = id,
            isDefault = stored is null,
            policy = stored ?? QuotaPolicyResolver.DefaultPolicy()
        });
    }

    // PUT /api/v1/admin/workspaces/{id}/policy — partial: omitted fields keep their current value.
    [HttpPut("workspaces/{id:guid}/policy")]
    public async Task<IActionResult> SetWorkspacePolicy(
        Guid id, [FromBody] SetWorkspacePolicyRequest req, CancellationToken ct)
    {
        var ws = await _db.Workspaces.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (ws is null) return NotFound(new { error = new { code = "workspace_not_found", message = "Workspace not found." } });

        var current = QuotaPolicyResolver.DeserializePolicy(ws.QuotaPolicyJson) ?? QuotaPolicyResolver.DefaultPolicy();

        var merged = new StoredPolicy(
            AllowedModels: req.AllowedModels ?? current.AllowedModels,
            UserWindows: req.UserWindows ?? current.UserWindows,
            WorkspaceWindows: req.WorkspaceWindows ?? current.WorkspaceWindows,
            RequestsPerMinute: req.RequestsPerMinute ?? current.RequestsPerMinute);

        if (Validate(merged.UserWindows, merged.AllowedModels) is { } invalid) return invalid;
        if (Validate(merged.WorkspaceWindows, null) is { } invalidWs) return invalidWs;
        if (merged.RequestsPerMinute is < 1 or > 10_000)
            return Invalid("requestsPerMinute must be between 1 and 10000.", "invalid_rate_limit");

        ws.QuotaPolicyJson = JsonSerializer.Serialize(merged, QuotaPolicyResolver.JsonOptions);

        _db.AuditLogs.Add(new AuditLog
        {
            OrganizationId = OrgId,
            UserId = ActorId,
            Action = "workspace_policy_set",
            Detail = $"workspace={id} userWindows={merged.UserWindows.Length} " +
                     $"workspaceWindows={merged.WorkspaceWindows.Length} models={merged.AllowedModels.Length}"
        });

        await _db.SaveChangesAsync(ct);
        _cache.InvalidateWorkspace(id);

        return Ok(new { workspaceId = id, policy = merged });
    }

    // ------------------------------------------------------------------ helpers

    private Task<(Membership? membership, IActionResult? error)> ResolveMembership(
        Guid userId, Guid? workspaceId, CancellationToken ct) =>
        MembershipLookup.ResolveAsync(_db, userId, workspaceId, ct);

    private IActionResult? Validate(QuotaWindow[]? windows, string[]? models)
    {
        if (windows is not null)
        {
            if (windows.Length is 0 or > MaxWindows)
                return Invalid($"Provide between 1 and {MaxWindows} windows.", "invalid_windows");

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var w in windows)
            {
                if (string.IsNullOrWhiteSpace(w.Name) || !WindowName.IsMatch(w.Name))
                    return Invalid($"Window name '{w.Name}' must match [A-Za-z0-9_-]{{1,32}} — it forms part of the Redis key.", "invalid_window_name");
                if (!seen.Add(w.Name))
                    return Invalid($"Duplicate window name '{w.Name}'.", "duplicate_window_name");
                if (w.TokenLimit < 1)
                    return Invalid($"Window '{w.Name}' must have a tokenLimit of at least 1.", "invalid_token_limit");
                if (w.WindowMinutes is < 1 or > MaxWindowMinutes)
                    return Invalid($"Window '{w.Name}' must have windowMinutes between 1 and {MaxWindowMinutes}.", "invalid_window_minutes");
            }
        }

        if (models is not null)
        {
            if (models.Length is 0 or > MaxModels)
                return Invalid($"Provide between 1 and {MaxModels} models.", "invalid_models");

            foreach (var m in models)
            {
                if (string.IsNullOrWhiteSpace(m))
                    return Invalid("Model names cannot be blank.", "invalid_models");
                if (!_router.IsSupported(m))
                    return Invalid($"No registered provider serves model '{m}'.", "unknown_model");
            }
        }

        return null;
    }

    private IActionResult Invalid(string message, string code) =>
        BadRequest(new { error = new { code, message } });
}
