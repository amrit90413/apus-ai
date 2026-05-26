using System.Security.Claims;
using Gateway.Api.Persistence;
using Gateway.Api.Quota;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace Gateway.Api.Gateway;

// Self-service endpoints backing `yourcompany-ai usage|sessions|models` and the user dashboard.
[ApiController]
[Route("api/v1/me")]
[Authorize]
public sealed class MeController : ControllerBase
{
    private readonly IQuotaPolicyResolver _policies;
    private readonly IConnectionMultiplexer _redis;
    private readonly GatewayDbContext _db;

    public MeController(IQuotaPolicyResolver policies, IConnectionMultiplexer redis, GatewayDbContext db)
    {
        _policies = policies; _redis = redis; _db = db;
    }

    private (Guid userId, Guid workspaceId, Guid sessionId) Identity()
    {
        return (
            Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!),
            Guid.Parse(User.FindFirstValue("workspace_id")!),
            Guid.Parse(User.FindFirstValue("session_id")!));
    }

    [HttpGet("usage")]
    public async Task<IActionResult> Usage(CancellationToken ct)
    {
        var (userId, workspaceId, _) = Identity();
        var policy = await _policies.ResolveAsync(new QuotaPrincipal(userId, workspaceId), ct);
        var db = _redis.GetDatabase();

        var windows = new List<object>();
        foreach (var w in policy.UserWindows)
        {
            var key = $"quota:user:{userId}:user:{w.Name}";
            var used = (long)(await db.StringGetAsync(key)).GetValueOrDefault();
            var ttl = await db.KeyTimeToLiveAsync(key);
            windows.Add(new { name = w.Name, used, limit = w.TokenLimit, resetInSeconds = (int)(ttl?.TotalSeconds ?? w.WindowSeconds) });
        }
        return Ok(new { windows });
    }

    [HttpGet("models")]
    public async Task<IActionResult> Models(CancellationToken ct)
    {
        var (userId, workspaceId, _) = Identity();
        var policy = await _policies.ResolveAsync(new QuotaPrincipal(userId, workspaceId), ct);
        return Ok(new { models = policy.AllowedModels });
    }

    [HttpGet("sessions")]
    public async Task<IActionResult> Sessions(CancellationToken ct)
    {
        var (userId, _, currentSessionId) = Identity();
        var sessions = await _db.Sessions
            .Where(s => s.UserId == userId && s.RevokedAt == null)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new { s.DeviceName, lastIp = s.LastIp, s.CreatedAt, current = s.Id == currentSessionId })
            .ToListAsync(ct);
        return Ok(new { sessions });
    }
}
