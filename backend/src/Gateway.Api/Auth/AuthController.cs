using System.Security.Cryptography;
using Gateway.Api.Auth;
using Gateway.Api.Domain;
using Gateway.Api.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Auth;

public sealed record LoginRequest(string Email, string Password, string DeviceName);
public sealed record RefreshRequest(string RefreshToken);
public sealed record TokenResponse(string AccessToken, string RefreshToken, DateTimeOffset AccessExpiresAt);

[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly GatewayDbContext _db;
    private readonly TokenService _tokens;
    private readonly JwtOptions _jwt;
    private readonly ILogger<AuthController> _log;

    public AuthController(GatewayDbContext db, TokenService tokens, JwtOptions jwt, ILogger<AuthController> log)
    {
        _db = db; _tokens = tokens; _jwt = jwt; _log = log;
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req, CancellationToken ct)
    {
        // Login is cross-tenant by design (no org context yet), so query without the
        // tenant filter via IgnoreQueryFilters. Password check is constant-time.
        var user = await _db.Users.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Email == req.Email && u.IsActive, ct);

        if (user is null || !PasswordHasher.Verify(req.Password, user.PasswordHash))
        {
            await Audit(user?.OrganizationId, user?.Id, "login_failed", req.Email);
            return Unauthorized(new { error = new { code = "invalid_credentials", message = "Email or password is incorrect." } });
        }

        // Pick the user's default/first workspace membership.
        var membership = await _db.Memberships.IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.UserId == user.Id, ct);
        if (membership is null)
            return Unauthorized(new { error = new { code = "no_workspace", message = "User has no workspace membership." } });

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var refresh = TokenService.GenerateRefreshToken();
        var session = new Session
        {
            OrganizationId = user.OrganizationId,
            UserId = user.Id,
            WorkspaceId = membership.WorkspaceId,
            DeviceName = req.DeviceName,
            DeviceFingerprint = Fingerprint(req.DeviceName, Request.Headers.UserAgent.ToString()),
            RefreshTokenHash = TokenService.Hash(refresh),
            LastIp = ip,                                   // anomaly detection only
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(_jwt.RefreshTokenDays)
        };
        _db.Sessions.Add(session);
        await _db.SaveChangesAsync(ct);

        var issued = _tokens.Issue(user, membership, session);
        await Audit(user.OrganizationId, user.Id, "login", $"device={req.DeviceName} ip={ip}");

        return Ok(new TokenResponse(issued.AccessToken, refresh, issued.AccessExpiresAt));
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest req, CancellationToken ct)
    {
        var hash = TokenService.Hash(req.RefreshToken);
        var session = await _db.Sessions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.RefreshTokenHash == hash, ct);

        if (session is null || !session.IsActive)
            return Unauthorized(new { error = new { code = "invalid_refresh", message = "Session expired. Please log in again." } });

        var user = await _db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == session.UserId, ct);
        var membership = await _db.Memberships.IgnoreQueryFilters()
            .FirstAsync(m => m.UserId == user.Id && m.WorkspaceId == session.WorkspaceId, ct);

        // Rotate the refresh token (detect token theft: an old token can't be reused).
        var newRefresh = TokenService.GenerateRefreshToken();
        session.RefreshTokenHash = TokenService.Hash(newRefresh);
        session.LastIp = HttpContext.Connection.RemoteIpAddress?.ToString();
        await _db.SaveChangesAsync(ct);

        var issued = _tokens.Issue(user, membership, session);
        return Ok(new TokenResponse(issued.AccessToken, newRefresh, issued.AccessExpiresAt));
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromBody] RefreshRequest req, CancellationToken ct)
    {
        var hash = TokenService.Hash(req.RefreshToken);
        var session = await _db.Sessions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.RefreshTokenHash == hash, ct);
        if (session is not null)
        {
            session.RevokedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        return NoContent();
    }

    private static string Fingerprint(string device, string ua)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{device}|{ua}"));
        return Convert.ToHexString(bytes)[..16];
    }

    private async Task Audit(Guid? org, Guid? user, string action, string? detail)
    {
        if (org is null) return;
        _db.AuditLogs.Add(new AuditLog
        {
            OrganizationId = org.Value, UserId = user, Action = action, Detail = detail,
            Ip = HttpContext.Connection.RemoteIpAddress?.ToString()
        });
        await _db.SaveChangesAsync();
    }
}
