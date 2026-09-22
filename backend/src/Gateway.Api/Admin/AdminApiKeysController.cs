using System.Security.Claims;
using Gateway.Api.Domain;
using Gateway.Api.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Gateway.Api.Admin;

/// <summary>Org admins can see and revoke any member's personal proxy keys.</summary>
[ApiController]
[Route("api/v1/admin/users/{id:guid}/keys")]
[Authorize(Policy = "OrgAdmin")]
public sealed class AdminApiKeysController : ControllerBase
{
    private readonly GatewayDbContext _db;
    private readonly IMemoryCache _cache;

    public AdminApiKeysController(GatewayDbContext db, IMemoryCache cache)
    {
        _db = db; _cache = cache;
    }

    private Guid OrgId => Guid.Parse(User.FindFirstValue("org_id")!);
    private Guid ActorId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<IActionResult> List(Guid id, CancellationToken ct)
    {
        // Tenant filter on ApiKeys guarantees a foreign user id yields nothing.
        if (!await _db.Users.AnyAsync(u => u.Id == id, ct)) return NotFound();

        var keys = await _db.ApiKeys.AsNoTracking()
            .Where(k => k.UserId == id)
            .OrderByDescending(k => k.CreatedAt)
            .Take(100)
            .Select(k => new { k.Id, k.Name, k.Prefix, k.WorkspaceId, k.CreatedAt, k.LastUsedAt, k.ExpiresAt, k.RevokedAt })
            .ToListAsync(ct);
        return Ok(new { userId = id, keys });
    }

    [HttpDelete("{keyId:guid}")]
    public async Task<IActionResult> Revoke(Guid id, Guid keyId, CancellationToken ct)
    {
        var key = await _db.ApiKeys.FirstOrDefaultAsync(k => k.Id == keyId && k.UserId == id, ct);
        if (key is null) return NotFound();
        if (key.RevokedAt is null)
        {
            key.RevokedAt = DateTimeOffset.UtcNow;
            _db.AuditLogs.Add(new AuditLog
            {
                OrganizationId = OrgId, UserId = ActorId, Action = "api_key_revoked_by_admin",
                Detail = $"targetUser={id} key={keyId}", Ip = HttpContext.Connection.RemoteIpAddress?.ToString()
            });
            await _db.SaveChangesAsync(ct);
            _cache.Remove($"apikey:{key.KeyHash}");
        }
        return NoContent();
    }
}
