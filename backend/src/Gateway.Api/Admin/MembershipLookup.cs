using Gateway.Api.Domain;
using Gateway.Api.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Admin;

/// <summary>
/// Resolves the (user, workspace) membership an admin operation targets. The DbContext
/// is tenant-filtered, so a user id from another organization resolves to nothing.
/// </summary>
public static class MembershipLookup
{
    public static async Task<(Membership? membership, IActionResult? error)> ResolveAsync(
        GatewayDbContext db, Guid userId, Guid? workspaceId, CancellationToken ct)
    {
        var memberships = await db.Memberships.Where(m => m.UserId == userId).ToListAsync(ct);

        if (memberships.Count == 0)
            return (null, new NotFoundObjectResult(new { error = new { code = "membership_not_found", message = "User has no workspace membership." } }));

        if (workspaceId is null)
        {
            // Don't guess when the user spans workspaces — quota is per (user, workspace).
            if (memberships.Count > 1)
                return (null, new BadRequestObjectResult(new { error = new { code = "workspace_required",
                    message = "User belongs to multiple workspaces. Pass ?workspaceId= to choose one." } }));
            return (memberships[0], null);
        }

        var match = memberships.FirstOrDefault(m => m.WorkspaceId == workspaceId.Value);
        return match is null
            ? (null, new NotFoundObjectResult(new { error = new { code = "membership_not_found", message = "User is not a member of that workspace." } }))
            : (match, null);
    }
}
