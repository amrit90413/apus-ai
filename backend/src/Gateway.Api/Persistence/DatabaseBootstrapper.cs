using Gateway.Api.Auth;
using Gateway.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Persistence;

public sealed class BootstrapOptions
{
    public bool Enabled { get; set; } = true;
    public string OrganizationName { get; set; } = "Default Organization";
    public string OrganizationSlug { get; set; } = "default";
    public string WorkspaceName { get; set; } = "Default";
    public string AdminEmail { get; set; } = "";
    public string AdminPassword { get; set; } = "";
    /// <summary>E.164, e.g. "919876543210". Required: admin logins are OTP-gated.</summary>
    public string AdminPhoneNumber { get; set; } = "";
}

/// <summary>
/// Creates the first organization, workspace, super admin, and membership when the
/// users table is empty. Without this there is no way into the system: AuthController
/// exposes no registration, and every admin endpoint requires an OrgAdmin JWT.
///
/// Runs once at startup and no-ops as soon as any user exists, so it is safe to leave
/// enabled across restarts and redeploys.
/// </summary>
public static class DatabaseBootstrapper
{
    public static async Task RunAsync(IServiceProvider services, BootstrapOptions opt, ILogger logger, CancellationToken ct = default)
    {
        if (!opt.Enabled)
        {
            logger.LogInformation("Bootstrap disabled; skipping first-admin seed.");
            return;
        }

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

        // ITenantContext defaults to null here, so the global query filters are
        // inactive and this sees every row regardless of organization.
        if (await db.Users.IgnoreQueryFilters().AnyAsync(ct))
        {
            logger.LogInformation("Users already exist; skipping first-admin seed.");
            return;
        }

        if (string.IsNullOrWhiteSpace(opt.AdminEmail) || string.IsNullOrWhiteSpace(opt.AdminPassword))
        {
            logger.LogWarning(
                "Database has no users and Bootstrap__AdminEmail / Bootstrap__AdminPassword are unset. " +
                "Nobody can log in. Set both and restart.");
            return;
        }

        // Login for Role >= OrgAdmin returns 422 no_phone without a number, so an
        // admin seeded without one could never obtain a token.
        if (string.IsNullOrWhiteSpace(opt.AdminPhoneNumber))
        {
            logger.LogError(
                "Bootstrap__AdminPhoneNumber is required — admin logins are OTP-gated over WhatsApp " +
                "and an account without a number cannot complete login. Skipping seed.");
            return;
        }

        if (opt.AdminPassword.Length < 12)
            logger.LogWarning("Bootstrap admin password is shorter than 12 characters.");

        var org = new Organization
        {
            Name = opt.OrganizationName,
            Slug = opt.OrganizationSlug,
            PlanCode = "free",
            IsActive = true
        };
        var workspace = new Workspace
        {
            OrganizationId = org.Id,
            Name = opt.WorkspaceName,
            IsActive = true
        };
        var admin = new User
        {
            OrganizationId = org.Id,
            Email = opt.AdminEmail,
            PasswordHash = PasswordHasher.Hash(opt.AdminPassword),
            PhoneNumber = opt.AdminPhoneNumber,
            PhoneVerified = true,
            IsActive = true
        };
        var membership = new Membership
        {
            OrganizationId = org.Id,
            UserId = admin.Id,
            WorkspaceId = workspace.Id,
            Role = Role.SuperAdmin
        };

        db.Organizations.Add(org);
        db.Workspaces.Add(workspace);
        db.Users.Add(admin);
        db.Memberships.Add(membership);
        db.AuditLogs.Add(new AuditLog
        {
            OrganizationId = org.Id,
            UserId = admin.Id,
            Action = "bootstrap_seeded",
            Detail = $"org={org.Slug} workspace={workspace.Name} admin={admin.Email}"
        });

        try
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Seeded first super admin {Email} in organization {Slug} (workspace {Workspace}).",
                admin.Email, org.Slug, workspace.Name);
        }
        catch (DbUpdateException ex)
        {
            // Another replica won the race on the unique email/slug index.
            logger.LogInformation(ex, "First-admin seed skipped; another instance seeded it first.");
        }
    }
}
