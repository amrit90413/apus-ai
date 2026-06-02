using System.Security.Claims;
using Gateway.Api.Domain;
using Gateway.Api.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Admin;

public sealed record CreateWorkspaceRequest(string Name);
public sealed record AddMemberRequest(Guid UserId, Role Role = Role.User);

[ApiController]
[Route("api/v1/admin/workspaces")]
[Authorize(Policy = "OrgAdmin")]
public sealed class AdminWorkspacesController : ControllerBase
{
    private readonly GatewayDbContext _db;

    public AdminWorkspacesController(GatewayDbContext db) => _db = db;

    private Guid OrgId => Guid.Parse(User.FindFirstValue("org_id")!);
    private Guid ActorId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // GET /api/v1/admin/workspaces
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var workspaces = await _db.Workspaces.ToListAsync(ct);
        var memberCounts = await _db.Memberships
            .GroupBy(m => m.WorkspaceId)
            .Select(g => new { WorkspaceId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var countMap = memberCounts.ToDictionary(x => x.WorkspaceId, x => x.Count);
        var result = workspaces.Select(w => new
        {
            w.Id,
            w.Name,
            w.IsActive,
            MemberCount = countMap.GetValueOrDefault(w.Id, 0)
        });

        return Ok(new { workspaces = result });
    }

    // GET /api/v1/admin/workspaces/{id}/members
    [HttpGet("{id:guid}/members")]
    public async Task<IActionResult> Members(Guid id, CancellationToken ct)
    {
        var members = await _db.Memberships
            .Where(m => m.WorkspaceId == id)
            .Join(_db.Users.IgnoreQueryFilters(),
                m => m.UserId,
                u => u.Id,
                (m, u) => new
                {
                    u.Id,
                    u.Email,
                    u.PhoneNumber,
                    u.IsActive,
                    Role = m.Role.ToString(),
                    MembershipId = m.Id
                })
            .ToListAsync(ct);

        return Ok(new { workspaceId = id, members });
    }

    // POST /api/v1/admin/workspaces
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateWorkspaceRequest req, CancellationToken ct)
    {
        var workspace = new Workspace
        {
            OrganizationId = OrgId,
            Name = req.Name,
            IsActive = true
        };
        _db.Workspaces.Add(workspace);

        _db.AuditLogs.Add(new AuditLog
        {
            OrganizationId = OrgId, UserId = ActorId,
            Action = "workspace_created", Detail = $"name={req.Name}"
        });

        await _db.SaveChangesAsync(ct);
        return Ok(new { workspaceId = workspace.Id, name = workspace.Name });
    }

    // POST /api/v1/admin/workspaces/{id}/members
    [HttpPost("{id:guid}/members")]
    public async Task<IActionResult> AddMember(Guid id, [FromBody] AddMemberRequest req, CancellationToken ct)
    {
        var workspace = await _db.Workspaces.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (workspace is null) return NotFound(new { error = "Workspace not found." });

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == req.UserId, ct);
        if (user is null) return NotFound(new { error = "User not found." });

        var existing = await _db.Memberships
            .AnyAsync(m => m.UserId == req.UserId && m.WorkspaceId == id, ct);
        if (existing)
            return Conflict(new { error = "User is already a member of this workspace." });

        _db.Memberships.Add(new Membership
        {
            OrganizationId = OrgId,
            UserId = req.UserId,
            WorkspaceId = id,
            Role = req.Role
        });

        _db.AuditLogs.Add(new AuditLog
        {
            OrganizationId = OrgId, UserId = ActorId,
            Action = "member_added", Detail = $"user={req.UserId} workspace={id} role={req.Role}"
        });

        await _db.SaveChangesAsync(ct);
        return Ok(new { userId = req.UserId, workspaceId = id, role = req.Role });
    }

    // DELETE /api/v1/admin/workspaces/{id}/members/{userId}
    [HttpDelete("{id:guid}/members/{userId:guid}")]
    public async Task<IActionResult> RemoveMember(Guid id, Guid userId, CancellationToken ct)
    {
        var membership = await _db.Memberships
            .FirstOrDefaultAsync(m => m.WorkspaceId == id && m.UserId == userId, ct);
        if (membership is null) return NotFound();

        _db.Memberships.Remove(membership);

        _db.AuditLogs.Add(new AuditLog
        {
            OrganizationId = OrgId, UserId = ActorId,
            Action = "member_removed", Detail = $"user={userId} workspace={id}"
        });

        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
