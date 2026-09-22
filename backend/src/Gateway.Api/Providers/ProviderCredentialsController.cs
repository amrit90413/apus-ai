using System.Security.Claims;
using System.Text.Json;
using Gateway.Api.Domain;
using Gateway.Api.Persistence;
using Gateway.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StackExchange.Redis;

namespace Gateway.Api.Providers;

public sealed record AddOrgApiKeyRequest(string Provider, string ApiKey);
public sealed record OAuthStartRequest(string Provider = "anthropic");
public sealed record OAuthCallbackRequest(string Code, string State);

/// <summary>
/// Organization-owned provider credentials. An org admin connects Claude either by
/// pasting an API key or, when Anthropic:OAuth is configured, by a browser login
/// (authorization code + PKCE). Secrets are never returned; only the hint is.
/// </summary>
[ApiController]
[Route("api/v1/admin/provider-credentials")]
[Authorize(Policy = "OrgAdmin")]
public sealed class ProviderCredentialsController : ControllerBase
{
    private static readonly TimeSpan StateTtl = TimeSpan.FromMinutes(10);

    private readonly IProviderCredentialService _credentials;
    private readonly OAuthTokenClient _oauth;
    private readonly AnthropicOAuthOptions _oauthOpt;
    private readonly IConnectionMultiplexer _redis;
    private readonly GatewayDbContext _db;
    private readonly ILogger<ProviderCredentialsController> _log;

    public ProviderCredentialsController(
        IProviderCredentialService credentials, OAuthTokenClient oauth, AnthropicOAuthOptions oauthOpt,
        IConnectionMultiplexer redis, GatewayDbContext db, ILogger<ProviderCredentialsController> log)
    {
        _credentials = credentials; _oauth = oauth; _oauthOpt = oauthOpt; _redis = redis; _db = db; _log = log;
    }

    private Guid OrgId => Guid.Parse(User.FindFirstValue("org_id")!);
    private Guid ActorId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private string ActorEmail => User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Email) ?? "admin";

    // GET /api/v1/admin/provider-credentials
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var rows = await _credentials.ListAsync(OrgId, ct);
        return Ok(new
        {
            credentials = rows,
            oauth = new { enabled = _oauthOpt.Enabled, provider = "anthropic" }
        });
    }

    // POST /api/v1/admin/provider-credentials/api-key
    [HttpPost("api-key")]
    public async Task<IActionResult> AddApiKey([FromBody] AddOrgApiKeyRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.ApiKey) || req.ApiKey.Length < 8 || req.ApiKey.Length > 512)
            return BadRequest(new { error = new { code = "invalid_key", message = "API key length is invalid." } });

        var provider = ProviderNames.Normalize(req.Provider);
        if (provider is null)
            return BadRequest(new { error = new { code = "unknown_provider", message = "Provider must be 'anthropic' or 'openai'." } });

        var id = await _credentials.AddApiKeyAsync(OrgId, provider, req.ApiKey, ActorId, ct);
        await Audit("provider_credential_added", $"id={id} provider={provider} kind=api_key", ct);
        return Ok(new { id });
    }

    // POST /api/v1/admin/provider-credentials/oauth/start  -> { authorizeUrl }
    // The browser is sent to authorizeUrl; the provider redirects back to
    // Anthropic:OAuth:RedirectUri with ?code=&state=, which the frontend posts to /oauth/callback.
    [HttpPost("oauth/start")]
    public async Task<IActionResult> OAuthStart([FromBody] OAuthStartRequest req, CancellationToken ct)
    {
        if (!_oauthOpt.Enabled)
            return Conflict(new { error = new { code = "oauth_not_configured", message = "OAuth login is not configured on this gateway. Paste an API key instead." } });
        if (ProviderNames.Normalize(req.Provider) != "anthropic")
            return BadRequest(new { error = new { code = "unknown_provider", message = "OAuth is only available for 'anthropic'." } });

        var state = Pkce.NewState();
        var verifier = Pkce.NewVerifier();
        var pending = new PendingOAuth(OrgId, ActorId, verifier, "anthropic");

        await _redis.GetDatabase().StringSetAsync(
            StateKey(state), JsonSerializer.Serialize(pending), StateTtl, When.NotExists);

        await Audit("provider_oauth_started", "provider=anthropic", ct);
        return Ok(new { authorizeUrl = _oauth.BuildAuthorizeUrl(state, Pkce.Challenge(verifier)), expiresInSeconds = (int)StateTtl.TotalSeconds });
    }

    // POST /api/v1/admin/provider-credentials/oauth/callback
    [HttpPost("oauth/callback")]
    public async Task<IActionResult> OAuthCallback([FromBody] OAuthCallbackRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Code) || string.IsNullOrWhiteSpace(req.State) || req.State.Length > 128)
            return BadRequest(new { error = new { code = "invalid_callback", message = "code and state are required." } });

        // Single-use: GETDEL so a replayed callback cannot exchange twice.
        var raw = await _redis.GetDatabase().StringGetDeleteAsync(StateKey(req.State));
        if (raw.IsNullOrEmpty)
            return BadRequest(new { error = new { code = "invalid_state", message = "Login attempt expired or was already used. Start again." } });

        var pending = JsonSerializer.Deserialize<PendingOAuth>(raw!);
        // The admin who finishes the flow must be the one who started it, in the same org.
        if (pending is null || pending.OrganizationId != OrgId || pending.ActorId != ActorId)
            return Forbid();

        OAuthTokenSet tokens;
        try
        {
            tokens = await _oauth.ExchangeCodeAsync(req.Code, pending.CodeVerifier, ct);
        }
        catch (OAuthGrantRejectedException ex)
        {
            await Audit("provider_oauth_failed", $"reason={ex.Message}", ct);
            return BadRequest(new { error = new { code = "oauth_rejected", message = "The provider rejected the login. Start again." } });
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning(ex, "OAuth code exchange failed for org {Org}", OrgId);
            return StatusCode(502, new { error = new { code = "oauth_unreachable", message = "Could not reach the provider. Try again." } });
        }

        var id = await _credentials.AddOAuthAsync(OrgId, pending.Provider, tokens, $"OAuth · {ActorEmail}", ActorId, ct);
        await Audit("provider_credential_added", $"id={id} provider={pending.Provider} kind=oauth", ct);
        return Ok(new { id, expiresAt = tokens.ExpiresAt });
    }

    // POST /api/v1/admin/provider-credentials/{id}/test
    [HttpPost("{id:guid}/test")]
    public async Task<IActionResult> Test(Guid id, CancellationToken ct)
    {
        var (ok, message) = await _credentials.ProbeAsync(OrgId, id, ct);
        return Ok(new { ok, message });
    }

    // DELETE /api/v1/admin/provider-credentials/{id}
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Remove(Guid id, CancellationToken ct)
    {
        if (!await _credentials.DeactivateAsync(OrgId, id, ct)) return NotFound();
        await Audit("provider_credential_removed", $"id={id}", ct);
        return NoContent();
    }

    private static string StateKey(string state) => $"oauth:state:{state}";

    private async Task Audit(string action, string detail, CancellationToken ct)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            OrganizationId = OrgId, UserId = ActorId, Action = action, Detail = detail,
            Ip = HttpContext.Connection.RemoteIpAddress?.ToString()
        });
        await _db.SaveChangesAsync(ct);
    }

    private sealed record PendingOAuth(Guid OrganizationId, Guid ActorId, string CodeVerifier, string Provider);
}
