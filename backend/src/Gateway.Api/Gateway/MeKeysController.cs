using System.Security.Claims;
using Gateway.Api.Auth;
using Gateway.Api.Domain;
using Gateway.Api.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Gateway.Api.Gateway;

public sealed record CreateKeyRequest(string Name);

/// <summary>
/// Personal keys for the /v1 proxy. Only a JWT session can mint one (never a key
/// itself), so a leaked key cannot spawn more keys. The plaintext is returned once.
/// </summary>
[ApiController]
[Route("api/v1/me/keys")]
[Authorize(AuthenticationSchemes = "Bearer")]
public sealed class MeKeysController : ControllerBase
{
    private const int MaxKeysPerUser = 20;

    private readonly GatewayDbContext _db;
    private readonly IMemoryCache _cache;

    public MeKeysController(GatewayDbContext db, IMemoryCache cache)
    {
        _db = db; _cache = cache;
    }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private Guid OrgId => Guid.Parse(User.FindFirstValue("org_id")!);
    private Guid WorkspaceId => Guid.Parse(User.FindFirstValue("workspace_id")!);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var keys = await _db.ApiKeys.AsNoTracking()
            .Where(k => k.UserId == UserId)
            .OrderByDescending(k => k.CreatedAt)
            .Take(100)
            .Select(k => new { k.Id, k.Name, k.Prefix, k.CreatedAt, k.LastUsedAt, k.ExpiresAt, k.RevokedAt })
            .ToListAsync(ct);
        return Ok(new { keys });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateKeyRequest req, CancellationToken ct)
    {
        var name = (req.Name ?? "").Trim();
        if (name.Length is 0 or > 80)
            return BadRequest(new { error = new { code = "invalid_name", message = "name must be 1-80 characters." } });

        var active = await _db.ApiKeys.CountAsync(k => k.UserId == UserId && k.RevokedAt == null, ct);
        if (active >= MaxKeysPerUser)
            return Conflict(new { error = new { code = "too_many_keys", message = $"You already have {MaxKeysPerUser} active keys. Revoke one first." } });

        var plain = ApiKeys.Generate();
        var key = new ApiKey
        {
            OrganizationId = OrgId,
            UserId = UserId,
            WorkspaceId = WorkspaceId,
            Name = name,
            KeyHash = ApiKeys.Hash(plain),
            Prefix = ApiKeys.DisplayPrefix(plain),
        };
        _db.ApiKeys.Add(key);
        _db.AuditLogs.Add(new AuditLog
        {
            OrganizationId = OrgId, UserId = UserId, Action = "api_key_created",
            Detail = $"key={key.Id} name={name}", Ip = HttpContext.Connection.RemoteIpAddress?.ToString()
        });
        await _db.SaveChangesAsync(ct);

        return StatusCode(201, new { id = key.Id, name = key.Name, key = plain, prefix = key.Prefix, createdAt = key.CreatedAt, expiresAt = key.ExpiresAt });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        var key = await _db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id && k.UserId == UserId, ct);
        if (key is null) return NotFound();
        if (key.RevokedAt is null)
        {
            key.RevokedAt = DateTimeOffset.UtcNow;
            _db.AuditLogs.Add(new AuditLog
            {
                OrganizationId = OrgId, UserId = UserId, Action = "api_key_revoked",
                Detail = $"key={id}", Ip = HttpContext.Connection.RemoteIpAddress?.ToString()
            });
            await _db.SaveChangesAsync(ct);
            _cache.Remove($"apikey:{key.KeyHash}");
        }
        return NoContent();
    }
}
