using System.Security.Claims;
using System.Security.Cryptography;
using Gateway.Api.Auth;
using Gateway.Api.Domain;
using Gateway.Api.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Admin;

public sealed record CreateUserRequest(
    string Email,
    string Password,
    string? PhoneNumber,
    Guid WorkspaceId,
    Role Role = Role.User);

public sealed record UpdateUserRequest(
    string? PhoneNumber,
    bool? IsActive,
    Role? Role);

[ApiController]
[Route("api/v1/admin/users")]
[Authorize(Policy = "OrgAdmin")]
public sealed class AdminUsersController : ControllerBase
{
    private readonly GatewayDbContext _db;
    private readonly ClickHouseClient _ch;

    public AdminUsersController(GatewayDbContext db, ClickHouseClient ch)
    {
        _db = db;
        _ch = ch;
    }

    private Guid OrgId => Guid.Parse(User.FindFirstValue("org_id")!);

    // GET /api/v1/admin/users
    // Returns all users in the org joined with their 30-day usage from ClickHouse.
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var users = await _db.Users
            .Include(u => u.Memberships)
            .OrderBy(u => u.Email)
            .ToListAsync(ct);

        var stats = await _ch.GetUserStatsAsync(OrgId, days: 30, ct);
        var statMap = stats.ToDictionary(s => s.UserId);

        var result = users.Select(u =>
        {
            statMap.TryGetValue(u.Id.ToString(), out var stat);
            var membership = u.Memberships.FirstOrDefault();
            return new
            {
                u.Id,
                u.Email,
                u.PhoneNumber,
                u.PhoneVerified,
                u.IsActive,
                u.CreatedAt,
                Role = membership?.Role.ToString() ?? "User",
                WorkspaceId = membership?.WorkspaceId,
                Usage = stat is null ? null : new
                {
                    stat.InputTokens,
                    stat.OutputTokens,
                    stat.CostUsd,
                    stat.Requests,
                    stat.LastActive
                }
            };
        });

        return Ok(new { users = result });
    }

    // GET /api/v1/admin/users/{id}/activity
    // Returns day-by-day activity for one user (last 14 days).
    [HttpGet("{id:guid}/activity")]
    public async Task<IActionResult> Activity(Guid id, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return NotFound();

        var activity = await _ch.GetUserDailyActivityAsync(id, days: 14, ct);
        return Ok(new { userId = id, email = user.Email, activity });
    }

    // POST /api/v1/admin/users
    // Creates a new user and assigns them to a workspace.
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest req, CancellationToken ct)
    {
        if (await _db.Users.AnyAsync(u => u.Email == req.Email, ct))
            return Conflict(new { error = new { code = "email_taken", message = "A user with this email already exists." } });

        var workspace = await _db.Workspaces.FirstOrDefaultAsync(w => w.Id == req.WorkspaceId, ct);
        if (workspace is null)
            return BadRequest(new { error = new { code = "workspace_not_found", message = "Workspace not found." } });

        var user = new User
        {
            OrganizationId = OrgId,
            Email = req.Email,
            PasswordHash = PasswordHasher.Hash(req.Password),
            PhoneNumber = req.PhoneNumber,
            PhoneVerified = false,
            IsActive = true,
        };
        _db.Users.Add(user);

        var membership = new Membership
        {
            OrganizationId = OrgId,
            UserId = user.Id,
            WorkspaceId = req.WorkspaceId,
            Role = req.Role,
        };
        _db.Memberships.Add(membership);

        _db.AuditLogs.Add(new AuditLog
        {
            OrganizationId = OrgId,
            UserId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!),
            Action = "user_created",
            Detail = $"email={req.Email} role={req.Role} workspace={req.WorkspaceId}"
        });

        await _db.SaveChangesAsync(ct);
        return Ok(new { userId = user.Id, email = user.Email, role = req.Role });
    }

    // PATCH /api/v1/admin/users/{id}
    // Update phone number, role, or active status.
    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateUserRequest req, CancellationToken ct)
    {
        var user = await _db.Users.Include(u => u.Memberships)
            .FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return NotFound();

        if (req.PhoneNumber is not null)
        {
            user.PhoneNumber = req.PhoneNumber;
            user.PhoneVerified = false; // re-verify after number change
        }

        if (req.IsActive.HasValue)
            user.IsActive = req.IsActive.Value;

        if (req.Role.HasValue)
        {
            var membership = user.Memberships.FirstOrDefault();
            if (membership is not null) membership.Role = req.Role.Value;
        }

        _db.AuditLogs.Add(new AuditLog
        {
            OrganizationId = OrgId,
            UserId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!),
            Action = "user_updated",
            Detail = $"targetUser={id} active={req.IsActive} role={req.Role}"
        });

        await _db.SaveChangesAsync(ct);
        return Ok(new { userId = id, updated = true });
    }

    // POST /api/v1/admin/users/{id}/revoke-sessions
    // Force-logs out the user from all devices immediately.
    [HttpPost("{id:guid}/revoke-sessions")]
    public async Task<IActionResult> RevokeSessions(Guid id, CancellationToken ct)
    {
        var sessions = await _db.Sessions
            .Where(s => s.UserId == id && s.RevokedAt == null)
            .ToListAsync(ct);

        foreach (var s in sessions)
            s.RevokedAt = DateTimeOffset.UtcNow;

        _db.AuditLogs.Add(new AuditLog
        {
            OrganizationId = OrgId,
            UserId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!),
            Action = "sessions_revoked",
            Detail = $"targetUser={id} count={sessions.Count}"
        });

        await _db.SaveChangesAsync(ct);
        return Ok(new { userId = id, sessionsRevoked = sessions.Count });
    }
}
