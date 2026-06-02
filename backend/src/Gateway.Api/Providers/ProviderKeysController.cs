using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Gateway.Api.Providers;

public sealed record AddKeyRequest(string Provider, string ApiKey);

[ApiController]
[Route("api/v1/admin/provider-keys")]
[Authorize(Policy = "SuperAdmin")]
public sealed class ProviderKeysController : ControllerBase
{
    private readonly IProviderKeyService _keys;

    public ProviderKeysController(IProviderKeyService keys) => _keys = keys;

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
        => Ok(new { keys = await _keys.ListAsync(ct) });

    [HttpPost]
    public async Task<IActionResult> Add([FromBody] AddKeyRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.ApiKey) || req.ApiKey.Length < 8)
            return BadRequest(new { error = new { code = "invalid_key", message = "API key is too short." } });

        var provider = req.Provider.ToLowerInvariant();
        if (provider is not ("anthropic" or "openai"))
            return BadRequest(new { error = new { code = "unknown_provider", message = "Provider must be 'anthropic' or 'openai'." } });

        var id = await _keys.AddKeyAsync(provider, req.ApiKey, ct);
        return Ok(new { id });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Remove(Guid id, CancellationToken ct)
    {
        await _keys.RemoveKeyAsync(id, ct);
        return NoContent();
    }
}
