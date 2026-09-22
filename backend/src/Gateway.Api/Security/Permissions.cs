using System.Security.Claims;
using Gateway.Api.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Gateway.Api.Security;

/// <summary>
/// Named capabilities. Endpoints authorize on these, not on role names, so adding a
/// role is a change to one table instead of an audit of every controller.
/// </summary>
public static class Permissions
{
    public const string ProviderView = "Provider.View";
    public const string ProviderConnect = "Provider.Connect";
    public const string ProviderDisconnect = "Provider.Disconnect";
    public const string ProviderTest = "Provider.Test";

    public const string AiUserCreate = "AIUser.Create";
    public const string AiUserUpdate = "AIUser.Update";
    public const string AiUserSuspend = "AIUser.Suspend";

    public const string AllowanceView = "Allowance.View";
    public const string AllowanceUpdate = "Allowance.Update";

    public const string ModelAccessView = "ModelAccess.View";
    public const string ModelAccessUpdate = "ModelAccess.Update";

    public const string UsageViewSelf = "Usage.ViewSelf";
    public const string UsageViewTenant = "Usage.ViewTenant";
    public const string UsageExport = "Usage.Export";

    public const string BillingView = "Billing.View";
    public const string BillingManage = "Billing.Manage";

    public const string PlatformAdmin = "Platform.Admin";

    public static readonly string[] All =
    {
        ProviderView, ProviderConnect, ProviderDisconnect, ProviderTest,
        AiUserCreate, AiUserUpdate, AiUserSuspend,
        AllowanceView, AllowanceUpdate,
        ModelAccessView, ModelAccessUpdate,
        UsageViewSelf, UsageViewTenant, UsageExport,
        BillingView, BillingManage, PlatformAdmin,
    };
}

/// <summary>
/// Role → permission table. Least privilege: an AI admin runs provider connections
/// and allowances but cannot change billing, and a billing admin is the mirror image.
/// </summary>
public static class RolePermissions
{
    private static readonly IReadOnlyDictionary<Role, HashSet<string>> Map = Build();

    public static bool Has(Role role, string permission) =>
        Map.TryGetValue(role, out var set) && set.Contains(permission);

    public static IReadOnlyCollection<string> For(Role role) =>
        Map.TryGetValue(role, out var set) ? set : Array.Empty<string>();

    private static Dictionary<Role, HashSet<string>> Build()
    {
        var member = new HashSet<string>(StringComparer.Ordinal) { Permissions.UsageViewSelf };

        var viewer = new HashSet<string>(member, StringComparer.Ordinal)
        {
            Permissions.UsageViewTenant, Permissions.ProviderView,
            Permissions.AllowanceView, Permissions.ModelAccessView,
        };

        var workspaceAdmin = new HashSet<string>(viewer, StringComparer.Ordinal)
        {
            Permissions.AllowanceUpdate, Permissions.ModelAccessUpdate,
            Permissions.AiUserUpdate, Permissions.UsageExport,
        };

        var aiAdmin = new HashSet<string>(workspaceAdmin, StringComparer.Ordinal)
        {
            Permissions.ProviderConnect, Permissions.ProviderDisconnect, Permissions.ProviderTest,
            Permissions.AiUserCreate, Permissions.AiUserSuspend,
        };

        var billingAdmin = new HashSet<string>(viewer, StringComparer.Ordinal)
        {
            Permissions.BillingView, Permissions.BillingManage,
            Permissions.AllowanceUpdate, Permissions.UsageExport,
        };

        var orgAdmin = new HashSet<string>(aiAdmin, StringComparer.Ordinal)
        {
            Permissions.BillingView, Permissions.BillingManage,
        };

        var superAdmin = new HashSet<string>(orgAdmin, StringComparer.Ordinal) { Permissions.PlatformAdmin };

        return new Dictionary<Role, HashSet<string>>
        {
            [Role.User] = member,
            [Role.Viewer] = viewer,
            [Role.WorkspaceAdmin] = workspaceAdmin,
            [Role.AiAdmin] = aiAdmin,
            [Role.BillingAdmin] = billingAdmin,
            [Role.OrgAdmin] = orgAdmin,
            [Role.SuperAdmin] = superAdmin,
        };
    }
}

public sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

/// <summary>
/// Checks the caller's role claim against the permission table. Backend authorization
/// is the only authorization: the dashboard hiding a button is a courtesy, not a control.
/// </summary>
public sealed class PermissionHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        foreach (var claim in context.User.FindAll(ClaimTypes.Role))
        {
            if (Enum.TryParse<Role>(claim.Value, ignoreCase: true, out var role) &&
                RolePermissions.Has(role, requirement.Permission))
            {
                context.Succeed(requirement);
                break;
            }
        }
        return Task.CompletedTask;
    }
}

/// <summary>
/// Turns `[Authorize(Policy = "perm:Provider.Connect")]` into a policy on demand, so
/// permissions do not have to be enumerated at startup. Unknown names fall through to
/// the default provider, which fails closed.
/// </summary>
public sealed class PermissionPolicyProvider : DefaultAuthorizationPolicyProvider
{
    public const string Prefix = "perm:";

    public PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : base(options) { }

    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (!policyName.StartsWith(Prefix, StringComparison.Ordinal))
            return await base.GetPolicyAsync(policyName);

        var permission = policyName[Prefix.Length..];
        if (!Permissions.All.Contains(permission, StringComparer.Ordinal))
            return null; // unknown permission => no policy => request denied

        return new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new PermissionRequirement(permission))
            .Build();
    }
}

/// <summary>Shorthand for `[Authorize(Policy = "perm:X")]`.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequirePermissionAttribute : AuthorizeAttribute
{
    public RequirePermissionAttribute(string permission) => Policy = PermissionPolicyProvider.Prefix + permission;
}
