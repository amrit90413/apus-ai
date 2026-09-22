using System.Security.Claims;
using Gateway.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ConnectionType = Gateway.Api.Domain.ConnectionType;

namespace Gateway.Api.Providers;

public sealed record AddKeyRequest(string Provider, string ApiKey);

/// <summary>
/// Platform-wide provider connections (OrganizationId = null). These are the fallback
/// for any organization that has not connected its own. Super admins only.
/// </summary>
[ApiController]
[Route("api/v1/admin/provider-keys")]
[Authorize(Policy = "SuperAdmin")]
public sealed class ProviderKeysController : ControllerBase
{
    private readonly IProviderConnectionService _connections;
    private readonly IAuditWriter _audit;

    public ProviderKeysController(IProviderConnectionService connections, IAuditWriter audit)
    {
        _connections = connections; _audit = audit;
    }

    private Guid ActorId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var rows = await _connections.ListAsync(null, ct);
        // Keep the response shape the super-admin dashboard renders.
        return Ok(new
        {
            keys = rows.Select(r => new
            {
                r.Id, r.Provider, KeyHint = r.Hint, Kind = r.ConnectionType,
                IsActive = r.Status is "connected" or "refreshing", r.Status, CreatedAt = r.ConnectedAt,
                r.LastValidatedAt, r.FailureCount,
            }),
            connections = rows,
        });
    }

    [HttpPost]
    public async Task<IActionResult> Add([FromBody] AddKeyRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.ApiKey) || req.ApiKey.Length is < 8 or > 512)
            return BadRequest(new { error = new { code = "invalid_key", message = "API key length is invalid." } });

        var provider = ProviderCatalog.Normalize(req.Provider);
        if (provider is null)
            return BadRequest(new { error = new { code = "unknown_provider", message = "Unknown provider." } });

        try
        {
            var result = await _connections.ConnectAsync(new ConnectRequest(
                null, provider, ConnectionType.ApiKey, req.ApiKey.Trim(),
                Extra: null, Config: null, DisplayName: "Platform fallback", ProviderAccountId: null), ActorId, ct);

            var (ok, message) = await _connections.ValidateAsync(null, result.Id, ct);
            await _audit.WriteAsync(AuditActions.ProviderConnected, AuditResources.ProviderConnection, result.Id.ToString(),
                after: new { provider, scope = "platform", validated = ok }, detail: $"platform fallback provider={provider}", ct: ct);

            return Ok(new { id = result.Id, validated = ok, message });
        }
        catch (ConnectionStateException ex)
        {
            return BadRequest(new { error = new { code = ex.Code, message = ex.Message } });
        }
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Remove(Guid id, CancellationToken ct)
    {
        await _connections.DisconnectAsync(null, id, ActorId, ct);
        await _audit.WriteAsync(AuditActions.ProviderDisconnected, AuditResources.ProviderConnection, id.ToString(),
            detail: "platform fallback removed", ct: ct);
        return NoContent();
    }
}

/// <summary>Kept for callers that still normalize provider names through this type.</summary>
public static class ProviderNames
{
    public static string? Normalize(string? provider) => ProviderCatalog.Normalize(provider);
}
