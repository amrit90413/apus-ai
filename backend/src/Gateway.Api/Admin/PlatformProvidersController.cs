using Gateway.Api.Domain;
using Gateway.Api.Persistence;
using Gateway.Api.Providers;
using Gateway.Api.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Admin;

/// <summary>
/// Cross-tenant provider health for the platform operator: which organizations have
/// connected what, and which connections need someone's attention.
///
/// Deliberately aggregate. It answers "is anything broken out there" without becoming
/// a way to browse individual tenants' configuration, and it returns no credential
/// material at all — not even the hint, which can name a customer's cloud account.
/// </summary>
[ApiController]
[Route("api/v1/admin/platform/providers")]
public sealed class PlatformProvidersController : ControllerBase
{
    private readonly GatewayDbContext _db;

    public PlatformProvidersController(GatewayDbContext db) => _db = db;

    [HttpGet]
    [RequirePermission(Permissions.PlatformAdmin)]
    public async Task<IActionResult> Health(CancellationToken ct)
    {
        // A platform admin's tenant scope is already null, so the query filters are
        // inactive; IgnoreQueryFilters makes that independent of how the caller was
        // scoped rather than relying on it.
        var rows = await _db.ProviderCredentials.IgnoreQueryFilters().AsNoTracking()
            .GroupBy(c => new { c.Provider, c.Status })
            .Select(g => new { g.Key.Provider, g.Key.Status, Count = g.Count() })
            .ToListAsync(ct);

        var needsAttention = await _db.ProviderCredentials.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.Status == ConnectionStatus.ReauthenticationRequired || c.Status == ConnectionStatus.Error)
            .OrderByDescending(c => c.LastFailureAt)
            .Take(100)
            .Select(c => new
            {
                c.Id,
                c.OrganizationId,
                c.Provider,
                Status = c.Status,
                c.FailureCount,
                c.LastFailureAt,
                c.LastValidatedAt,
            })
            .ToListAsync(ct);

        var providers = ProviderCatalog.Descriptors.Select(descriptor =>
        {
            var byStatus = rows.Where(r => r.Provider == descriptor.Id).ToList();
            return new
            {
                provider = descriptor.Id,
                displayName = descriptor.DisplayName,
                connected = byStatus.Where(r => r.Status == ConnectionStatus.Connected).Sum(r => r.Count),
                needsReconnect = byStatus.Where(r => r.Status == ConnectionStatus.ReauthenticationRequired).Sum(r => r.Count),
                error = byStatus.Where(r => r.Status == ConnectionStatus.Error).Sum(r => r.Count),
                disabled = byStatus.Where(r => r.Status == ConnectionStatus.Disabled).Sum(r => r.Count),
                revoked = byStatus.Where(r => r.Status == ConnectionStatus.Revoked).Sum(r => r.Count),
            };
        });

        return Ok(new
        {
            providers,
            needsAttention = needsAttention.Select(c => new
            {
                c.Id,
                c.OrganizationId,
                c.Provider,
                status = ConnectionStatuses.Wire(c.Status),
                c.FailureCount,
                c.LastFailureAt,
                c.LastValidatedAt,
            }),
        });
    }
}
