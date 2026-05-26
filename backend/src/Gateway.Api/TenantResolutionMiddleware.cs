using System.Security.Claims;
using Gateway.Api.Domain;
using Gateway.Api.Persistence;

namespace Gateway.Api;

/// <summary>
/// Reads org_id + role from the validated JWT and populates ITenantContext so the
/// EF global query filters scope every query to the caller's organization.
/// SuperAdmin gets CurrentOrganizationId = null (cross-tenant access).
/// </summary>
public sealed class TenantResolutionMiddleware
{
    private readonly RequestDelegate _next;
    public TenantResolutionMiddleware(RequestDelegate next) => _next = next;

    public async Task Invoke(HttpContext ctx, ITenantContext tenant)
    {
        var user = ctx.User;
        if (user.Identity?.IsAuthenticated == true)
        {
            var isSuper = user.IsInRole(nameof(Role.SuperAdmin));
            var orgClaim = user.FindFirstValue("org_id");
            tenant.CurrentOrganizationId =
                isSuper ? null : (Guid.TryParse(orgClaim, out var g) ? g : null);
        }
        await _next(ctx);
    }
}
