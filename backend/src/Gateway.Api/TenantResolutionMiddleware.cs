using System.Security.Claims;
using Gateway.Api.Auth;
using Gateway.Api.Domain;
using Gateway.Api.Persistence;
using Microsoft.AspNetCore.Authentication;

namespace Gateway.Api;

/// <summary>
/// Reads org_id + role from the authenticated principal and populates ITenantContext
/// so the EF global query filters scope every query to the caller's organization.
/// SuperAdmin gets CurrentOrganizationId = null (cross-tenant access).
///
/// Runs after UseAuthentication. The default scheme is JWT; the /v1 proxy accepts
/// personal keys, whose scheme only runs at authorization time — too late for the
/// tenant scope — so it is authenticated here explicitly for those paths.
/// </summary>
public sealed class TenantResolutionMiddleware
{
    private readonly RequestDelegate _next;
    public TenantResolutionMiddleware(RequestDelegate next) => _next = next;

    public async Task Invoke(HttpContext ctx, ITenantContext tenant)
    {
        var user = ctx.User;
        if (user.Identity?.IsAuthenticated != true && ctx.Request.Path.StartsWithSegments("/v1"))
        {
            var result = await ctx.AuthenticateAsync(ApiKeys.Scheme);
            if (result.Succeeded) { ctx.User = result.Principal; user = ctx.User; }
        }

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
