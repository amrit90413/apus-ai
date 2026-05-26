using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Gateway.Api.Domain;
using Microsoft.IdentityModel.Tokens;

namespace Gateway.Api.Auth;

public sealed class JwtOptions
{
    public string Issuer { get; set; } = "yourcompany-ai";
    public string Audience { get; set; } = "yourcompany-ai-cli";
    public string SigningKey { get; set; } = "";   // from K8s secret; min 32 bytes
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 30;
}

public sealed record IssuedTokens(string AccessToken, string RefreshToken, DateTimeOffset AccessExpiresAt);

public sealed class TokenService
{
    private readonly JwtOptions _opt;
    private readonly SymmetricSecurityKey _key;

    public TokenService(JwtOptions opt)
    {
        _opt = opt;
        _key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(opt.SigningKey));
    }

    /// <summary>
    /// Short-lived access token carries the full identity needed by the gateway:
    /// user, org (for tenant filter), workspace + session (for quota ownership).
    /// </summary>
    public IssuedTokens Issue(User user, Membership membership, Session session)
    {
        var now = DateTimeOffset.UtcNow;
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new("org_id", user.OrganizationId.ToString()),
            new("workspace_id", membership.WorkspaceId.ToString()),
            new("session_id", session.Id.ToString()),
            new(ClaimTypes.Role, membership.Role.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var token = new JwtSecurityToken(
            issuer: _opt.Issuer,
            audience: _opt.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: now.AddMinutes(_opt.AccessTokenMinutes).UtcDateTime,
            signingCredentials: new SigningCredentials(_key, SecurityAlgorithms.HmacSha256));

        var access = new JwtSecurityTokenHandler().WriteToken(token);
        return new IssuedTokens(access, GenerateRefreshToken(), now.AddMinutes(_opt.AccessTokenMinutes));
    }

    /// <summary>Opaque high-entropy refresh token; only its SHA-256 hash is stored.</summary>
    public static string GenerateRefreshToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(64);
        return Convert.ToBase64String(bytes);
    }

    public static string Hash(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes);
    }

    public TokenValidationParameters ValidationParameters => new()
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = _opt.Issuer,
        ValidAudience = _opt.Audience,
        IssuerSigningKey = _key,
        ClockSkew = TimeSpan.FromSeconds(30)
    };
}
