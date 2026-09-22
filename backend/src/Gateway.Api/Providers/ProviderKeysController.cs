using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Gateway.Api.Providers;

public sealed record AddKeyRequest(string Provider, string ApiKey);

/// <summary>
/// Platform-wide provider keys (OrganizationId = null). These are the fallback for any
/// organization that has not connected its own credential. Super admins only.
/// </summary>
[ApiController]
[Route("api/v1/admin/provider-keys")]
[Authorize(Policy = "SuperAdmin")]
public sealed class ProviderKeysController : ControllerBase
{
    private readonly IProviderCredentialService _credentials;

    public ProviderKeysController(IProviderCredentialService credentials) => _credentials = credentials;

    private Guid ActorId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var rows = await _credentials.ListAsync(null, ct);
        // Keep the original response shape the super-admin dashboard renders.
        return Ok(new { keys = rows.Select(r => new { r.Id, r.Provider, KeyHint = r.Hint, r.Kind, r.IsActive, r.CreatedAt }) });
    }

    [HttpPost]
    public async Task<IActionResult> Add([FromBody] AddKeyRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.ApiKey) || req.ApiKey.Length < 8)
            return BadRequest(new { error = new { code = "invalid_key", message = "API key is too short." } });

        var provider = ProviderNames.Normalize(req.Provider);
        if (provider is null)
            return BadRequest(new { error = new { code = "unknown_provider", message = "Provider must be 'anthropic' or 'openai'." } });

        var id = await _credentials.AddApiKeyAsync(null, provider, req.ApiKey, ActorId, ct);
        return Ok(new { id });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Remove(Guid id, CancellationToken ct)
    {
        await _credentials.DeactivateAsync(null, id, ct);
        return NoContent();
    }
}

public static class ProviderNames
{
    public static string? Normalize(string? provider)
    {
        var p = provider?.Trim().ToLowerInvariant();
        return p is "anthropic" or "openai" ? p : null;
    }
}
