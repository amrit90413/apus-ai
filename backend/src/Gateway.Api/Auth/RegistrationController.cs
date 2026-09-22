using System.Text.RegularExpressions;
using Gateway.Api.Domain;
using Gateway.Api.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Auth;

public sealed class RegistrationOptions
{
    /// <summary>Self-service org signup. Off by default: on a private deployment the bootstrap admin creates everyone.</summary>
    public bool Enabled { get; set; } = false;
    /// <summary>Optional shared secret a signup must present (invite-only public deployments).</summary>
    public string? InviteCode { get; set; }
    public int MinPasswordLength { get; set; } = 12;
}

public sealed record RegisterRequest(
    string OrganizationName,
    string Email,
    string Password,
    string? PhoneNumber,
    string? InviteCode);

/// <summary>
/// Creates an organization, its default workspace, and the first OrgAdmin in one
/// transaction. The new admin then logs in through the normal /auth/login flow (OTP
/// applies when WhatsApp is enabled, which is why a phone number is required then).
/// </summary>
[ApiController]
[Route("api/v1/auth")]
public sealed partial class RegistrationController : ControllerBase
{
    private readonly GatewayDbContext _db;
    private readonly RegistrationOptions _opt;
    private readonly WhatsAppOptions _whatsapp;
    private readonly ILogger<RegistrationController> _log;

    public RegistrationController(GatewayDbContext db, RegistrationOptions opt, WhatsAppOptions whatsapp, ILogger<RegistrationController> log)
    {
        _db = db; _opt = opt; _whatsapp = whatsapp; _log = log;
    }

    [HttpGet("register")]
    public IActionResult Availability() =>
        Ok(new { enabled = _opt.Enabled, inviteCodeRequired = !string.IsNullOrEmpty(_opt.InviteCode), phoneRequired = _whatsapp.Enabled, minPasswordLength = _opt.MinPasswordLength });

    [HttpPost("register")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest req, CancellationToken ct)
    {
        if (!_opt.Enabled)
            return NotFound(new { error = new { code = "registration_disabled", message = "Self-service signup is disabled. Ask the platform administrator for an account." } });

        if (!string.IsNullOrEmpty(_opt.InviteCode) &&
            !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(req.InviteCode ?? ""), System.Text.Encoding.UTF8.GetBytes(_opt.InviteCode)))
            return Forbidden("invalid_invite", "Invite code is invalid.");

        var orgName = req.OrganizationName?.Trim() ?? "";
        var email = req.Email?.Trim().ToLowerInvariant() ?? "";
        var (phone, phoneError) = PhoneNumbers.Normalize(req.PhoneNumber);

        if (orgName.Length is < 2 or > 80)
            return Invalid("invalid_organization", "Organization name must be 2-80 characters.");
        if (!EmailPattern().IsMatch(email) || email.Length > 254)
            return Invalid("invalid_email", "Enter a valid email address.");
        if (req.Password is null || req.Password.Length < _opt.MinPasswordLength || req.Password.Length > 256)
            return Invalid("weak_password", $"Password must be at least {_opt.MinPasswordLength} characters.");
        if (phoneError is not null)
            return Invalid("invalid_phone", phoneError);
        if (_whatsapp.Enabled && phone is null)
            return Invalid("phone_required", "Admin logins are verified over WhatsApp; a phone number is required.");

        // Email is unique per organization in the schema, but a signup email must be
        // globally unique so login (which has no org context) stays unambiguous.
        if (await _db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email, ct))
            return Conflict(new { error = new { code = "email_taken", message = "An account with this email already exists." } });

        var slug = await UniqueSlugAsync(Slugify(orgName), ct);

        var org = new Organization { Name = orgName, Slug = slug, PlanCode = "free", IsActive = true };
        var workspace = new Workspace { OrganizationId = org.Id, Name = "Default", IsActive = true };
        var admin = new User
        {
            OrganizationId = org.Id,
            Email = email,
            PasswordHash = PasswordHasher.Hash(req.Password),
            PhoneNumber = phone,
            PhoneVerified = false,
            IsActive = true,
        };
        var membership = new Membership { OrganizationId = org.Id, UserId = admin.Id, WorkspaceId = workspace.Id, Role = Role.OrgAdmin };

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        _db.Organizations.Add(org);
        _db.Workspaces.Add(workspace);
        _db.Users.Add(admin);
        _db.Memberships.Add(membership);
        _db.AuditLogs.Add(new AuditLog
        {
            OrganizationId = org.Id, UserId = admin.Id, Action = "organization_registered",
            Detail = $"org={slug} admin={email}", Ip = HttpContext.Connection.RemoteIpAddress?.ToString()
        });

        try
        {
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Lost a race on the unique email/slug index.
            _log.LogInformation(ex, "Registration conflict for {Email}", email);
            return Conflict(new { error = new { code = "email_taken", message = "An account with this email already exists." } });
        }

        _log.LogInformation("Registered organization {Slug} with admin {Email}", slug, email);
        return StatusCode(201, new { organizationId = org.Id, slug, workspaceId = workspace.Id, userId = admin.Id, next = "login" });
    }

    /// <summary>Lowercase, hyphenated, ASCII-only; "Acme Corp!" -> "acme-corp".</summary>
    public static string Slugify(string name)
    {
        var s = NonSlug().Replace(name.ToLowerInvariant(), "-").Trim('-');
        s = Regex.Replace(s, "-{2,}", "-");
        if (s.Length > 40) s = s[..40].TrimEnd('-');
        return s.Length == 0 ? "org" : s;
    }

    private async Task<string> UniqueSlugAsync(string baseSlug, CancellationToken ct)
    {
        var taken = await _db.Organizations.Where(o => o.Slug.StartsWith(baseSlug)).Select(o => o.Slug).ToListAsync(ct);
        if (!taken.Contains(baseSlug)) return baseSlug;
        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{baseSlug}-{i}";
            if (!taken.Contains(candidate)) return candidate;
        }
        return $"{baseSlug}-{Guid.NewGuid():N}"[..48];
    }

    private IActionResult Invalid(string code, string message) => BadRequest(new { error = new { code, message } });
    private IActionResult Forbidden(string code, string message) => StatusCode(403, new { error = new { code, message } });

    [GeneratedRegex(@"^[^\s@]+@[^\s@]+\.[^\s@]+$")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonSlug();
}
