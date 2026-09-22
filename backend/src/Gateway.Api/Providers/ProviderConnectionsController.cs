using System.Security.Claims;
using System.Text.Json;
using Gateway.Api.Domain;
using Gateway.Api.Providers.Upstream;
using Gateway.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StackExchange.Redis;
using ConnectionType = Gateway.Api.Domain.ConnectionType;

namespace Gateway.Api.Providers;

/// <summary>
/// Everything an admin can send when connecting a provider. Only the fields that
/// matter for the chosen method are read; the rest are ignored.
/// </summary>
public sealed record ConnectProviderRequest(
    string? ConnectionType,
    string? ApiKey,
    // AWS Bedrock
    string? AccessKeyId,
    string? SecretAccessKey,
    string? SessionToken,
    string? Region,
    // Google Vertex AI
    string? ServiceAccountJson,
    string? Project,
    string? Location,
    // Common
    string? BaseUrl,
    string? DisplayName,
    string? ConnectionPurpose);

public sealed record OAuthStartRequest(string Provider = "anthropic");
public sealed record OAuthCallbackRequest(string Code, string State);
public sealed record AddOrgApiKeyRequest(string Provider, string ApiKey);

/// <summary>
/// Organization-owned provider connections.
///
/// An admin connects a provider here by pasting an API credential, by supplying cloud
/// credentials (Bedrock / Vertex), or — where the operator has been issued an OAuth
/// client by that provider — by a browser login. Secrets are encrypted before they
/// touch storage and are never returned by any endpoint; only a hint is.
///
/// The legacy `/api/v1/admin/provider-credentials` route is kept alongside the new one
/// so a rolling deploy with older dashboard pods keeps working.
/// </summary>
[ApiController]
[Route("api/v1/provider-connections")]
[Route("api/v1/admin/provider-credentials")]
[Authorize]
public sealed class ProviderConnectionsController : ControllerBase
{
    private static readonly TimeSpan StateTtl = TimeSpan.FromMinutes(10);
    private const string FlowCookie = "apus_oauth_flow";

    private readonly IProviderConnectionService _connections;
    private readonly OAuthTokenClient _oauth;
    private readonly ProviderOAuthRegistry _oauthRegistry;
    private readonly IConnectionMultiplexer _redis;
    private readonly IAuditWriter _audit;
    private readonly IFeatureFlags _features;
    private readonly ILogger<ProviderConnectionsController> _log;

    public ProviderConnectionsController(
        IProviderConnectionService connections, OAuthTokenClient oauth, ProviderOAuthRegistry oauthRegistry,
        IConnectionMultiplexer redis, IAuditWriter audit, IFeatureFlags features,
        ILogger<ProviderConnectionsController> log)
    {
        _connections = connections; _oauth = oauth; _oauthRegistry = oauthRegistry;
        _redis = redis; _audit = audit; _features = features; _log = log;
    }

    private Guid OrgId => Guid.Parse(User.FindFirstValue("org_id")!);
    private Guid ActorId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private string ActorEmail => User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Email) ?? "admin";

    // --------------------------------------------------------------------- list

    /// <summary>The tenant's connections plus the catalog the connect UI renders from.</summary>
    [HttpGet]
    [RequirePermission(Permissions.ProviderView)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var rows = await _connections.ListAsync(OrgId, ct);
        var catalog = ProviderCatalog.Descriptors.Select(p => new
        {
            id = p.Id,
            displayName = p.DisplayName,
            connectionTypes = p.ConnectionTypes.Select(ProviderConnectionService.WireType),
            oauthAvailable = _oauthRegistry.IsEnabled(p.Id) && _features.IsEnabled(FeatureFlagNames.ProviderOAuth, OrgId),
            requiredConfig = p.RequiredConfigKeys,
            docsUrl = p.DocsUrl,
        });

        return Ok(new
        {
            connections = rows,
            // Legacy field name the pre-003 dashboard reads.
            credentials = rows,
            providers = catalog,
            oauth = new { enabled = _oauthRegistry.IsEnabled(ProviderCatalog.Anthropic), provider = "anthropic" },
            features = _features.Snapshot(OrgId),
        });
    }

    // ------------------------------------------------------------------ connect

    /// <summary>Connects a provider with a credential the admin supplies (no OAuth).</summary>
    [HttpPost("{provider}/connect")]
    [RequirePermission(Permissions.ProviderConnect)]
    public async Task<IActionResult> Connect(string provider, [FromBody] ConnectProviderRequest req, CancellationToken ct)
    {
        if (!_features.IsEnabled(FeatureFlagNames.ProviderConnections, OrgId))
            return Error(StatusCodes.Status403Forbidden, "feature_disabled", "Provider connections are not enabled for your organization yet.");

        var descriptor = ProviderCatalog.Find(provider);
        if (descriptor is null)
            return Error(StatusCodes.Status400BadRequest, "unknown_provider", $"'{provider}' is not a supported provider.");

        var type = req.ConnectionType is null
            ? descriptor.ConnectionTypes.First(t => t != ConnectionType.OAuth)
            : ProviderConnectionService.ParseType(req.ConnectionType);

        if (type == ConnectionType.OAuth)
            return Error(StatusCodes.Status400BadRequest, "use_oauth_flow", "Start an OAuth connection with /oauth/start instead.");

        var build = BuildConnectRequest(descriptor, type, req);
        if (build.Error is not null) return build.Error;

        try
        {
            var result = await _connections.ConnectAsync(build.Request!, ActorId, ct);

            // Validate immediately so the admin learns now, not on a user's first request.
            var (ok, message) = await _connections.ValidateAsync(OrgId, result.Id, ct);

            await _audit.WriteAsync(AuditActions.ProviderConnected, AuditResources.ProviderConnection, result.Id.ToString(),
                after: new { provider = descriptor.Id, connectionType = ProviderConnectionService.WireType(type), hint = result.Hint, validated = ok },
                detail: $"provider={descriptor.Id} type={ProviderConnectionService.WireType(type)} validated={ok}", ct: ct);

            var view = await _connections.GetAsync(OrgId, result.Id, ct);
            return Ok(new { id = result.Id, status = view?.Status ?? result.Status, hint = result.Hint, validated = ok, message, connection = view });
        }
        catch (ConnectionStateException ex)
        {
            return Error(StatusCodes.Status400BadRequest, ex.Code, ex.Message);
        }
    }

    /// <summary>Legacy shape: POST /api-key { provider, apiKey }.</summary>
    [HttpPost("api-key")]
    [RequirePermission(Permissions.ProviderConnect)]
    public Task<IActionResult> AddApiKey([FromBody] AddOrgApiKeyRequest req, CancellationToken ct) =>
        Connect(req.Provider, new ConnectProviderRequest(
            ConnectionType: "api_key", ApiKey: req.ApiKey,
            AccessKeyId: null, SecretAccessKey: null, SessionToken: null, Region: null,
            ServiceAccountJson: null, Project: null, Location: null,
            BaseUrl: null, DisplayName: null, ConnectionPurpose: null), ct);

    /// <summary>Validates and shapes the per-method credential fields.</summary>
    private (ConnectRequest? Request, IActionResult? Error) BuildConnectRequest(
        ProviderDescriptor descriptor, ConnectionType type, ConnectProviderRequest req)
    {
        var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(req.BaseUrl)) config["baseUrl"] = req.BaseUrl.Trim();

        switch (type)
        {
            case ConnectionType.ApiKey:
            {
                var key = req.ApiKey?.Trim();
                if (string.IsNullOrWhiteSpace(key) || key.Length is < 8 or > 512)
                    return (null, Error(StatusCodes.Status400BadRequest, "invalid_key", "API key length is invalid."));
                return (new ConnectRequest(OrgId, descriptor.Id, type, key, null, config,
                    req.DisplayName, null, Purpose(req)), null);
            }

            case ConnectionType.AwsBedrock:
            {
                var accessKeyId = req.AccessKeyId?.Trim();
                var secret = req.SecretAccessKey?.Trim();
                var region = req.Region?.Trim();
                if (string.IsNullOrWhiteSpace(accessKeyId) || string.IsNullOrWhiteSpace(secret))
                    return (null, Error(StatusCodes.Status400BadRequest, "invalid_key", "An AWS access key id and secret access key are required."));
                if (string.IsNullOrWhiteSpace(region) || !IsSafeToken(region, 32))
                    return (null, Error(StatusCodes.Status400BadRequest, "missing_config", "A valid AWS region is required, e.g. us-east-1."));

                config["region"] = region;
                var extra = new Dictionary<string, string> { ["accessKeyId"] = accessKeyId };
                if (!string.IsNullOrWhiteSpace(req.SessionToken)) extra["sessionToken"] = req.SessionToken.Trim();

                return (new ConnectRequest(OrgId, descriptor.Id, type, secret, extra, config,
                    req.DisplayName, accessKeyId, Purpose(req)), null);
            }

            case ConnectionType.GoogleVertex:
            {
                var json = req.ServiceAccountJson?.Trim();
                if (string.IsNullOrWhiteSpace(json))
                    return (null, Error(StatusCodes.Status400BadRequest, "invalid_key", "A Google service-account key (JSON) is required."));

                var account = GoogleServiceAccount.TryParse(json);
                if (account is null)
                    return (null, Error(StatusCodes.Status400BadRequest, "invalid_key",
                        "That does not look like a service-account key: client_email and private_key are required."));

                var project = (req.Project ?? account.ProjectId)?.Trim();
                var location = req.Location?.Trim();
                if (string.IsNullOrWhiteSpace(project) || !IsSafeToken(project, 64))
                    return (null, Error(StatusCodes.Status400BadRequest, "missing_config", "A valid Google Cloud project id is required."));
                if (string.IsNullOrWhiteSpace(location) || !IsSafeToken(location, 32))
                    return (null, Error(StatusCodes.Status400BadRequest, "missing_config", "A valid Vertex AI location is required, e.g. us-central1."));

                config["project"] = project;
                config["location"] = location;
                return (new ConnectRequest(OrgId, descriptor.Id, type, json, null, config,
                    req.DisplayName, account.ClientEmail, Purpose(req)), null);
            }

            default:
                return (null, Error(StatusCodes.Status400BadRequest, "unsupported_connection_type",
                    $"{descriptor.DisplayName} does not support that connection method."));
        }
    }

    // -------------------------------------------------------------------- OAuth

    /// <summary>
    /// Begins a provider login. Creates the single-use state + PKCE verifier server
    /// side and binds the flow to this browser with an HttpOnly cookie, so a callback
    /// forged from elsewhere cannot attach an attacker's provider account to this tenant.
    /// </summary>
    [HttpPost("{provider}/oauth/start")]
    [HttpGet("{provider}/oauth/start")]
    [RequirePermission(Permissions.ProviderConnect)]
    public async Task<IActionResult> OAuthStart(string provider, CancellationToken ct)
    {
        var descriptor = ProviderCatalog.Find(provider);
        if (descriptor is null || !descriptor.Supports(ConnectionType.OAuth))
            return Error(StatusCodes.Status400BadRequest, "unknown_provider", "OAuth is not available for that provider.");

        if (!_features.IsEnabled(FeatureFlagNames.ProviderOAuth, OrgId))
            return Error(StatusCodes.Status403Forbidden, "feature_disabled", "OAuth connections are not enabled for your organization yet.");

        var opt = _oauthRegistry.For(descriptor.Id);
        if (opt is null)
            return Error(StatusCodes.Status409Conflict, "oauth_not_configured",
                $"OAuth login is not configured for {descriptor.DisplayName} on this gateway. Connect with a credential instead.");

        var state = Pkce.NewState();
        var verifier = Pkce.NewVerifier();
        var flowNonce = Pkce.NewState();
        var pending = new PendingOAuth(OrgId, ActorId, verifier, descriptor.Id, flowNonce);

        await _redis.GetDatabase().StringSetAsync(StateKey(state), JsonSerializer.Serialize(pending), StateTtl, When.NotExists);

        Response.Cookies.Append(FlowCookie, flowNonce, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,  // Lax so it survives the provider's top-level redirect back
            Path = "/",
            MaxAge = StateTtl,
        });

        await _audit.WriteAsync(AuditActions.OAuthStarted, AuditResources.ProviderConnection, null,
            detail: $"provider={descriptor.Id}", ct: ct);

        return Ok(new
        {
            authorizeUrl = OAuthTokenClient.BuildAuthorizeUrl(opt, state, Pkce.Challenge(verifier)),
            expiresInSeconds = (int)StateTtl.TotalSeconds,
        });
    }

    /// <summary>
    /// The browser redirect target. Anonymous by necessity — the provider sends the
    /// admin here directly — so the single-use state plus the flow cookie are what
    /// authenticate it.
    /// </summary>
    [HttpGet("{provider}/oauth/callback")]
    [AllowAnonymous]
    public async Task<IActionResult> OAuthCallbackRedirect(
        string provider, [FromQuery] string? code, [FromQuery] string? state,
        [FromQuery] string? error, CancellationToken ct)
    {
        var descriptor = ProviderCatalog.Find(provider);
        if (descriptor is null) return NotFound();
        var completion = CompletionUrl(descriptor.Id);

        if (!string.IsNullOrEmpty(error))
            return Redirect($"{completion}?provider={descriptor.Id}&error=provider_denied");

        var outcome = await ExchangeAsync(descriptor, code, state, requireJwtActor: false, ct);
        Response.Cookies.Delete(FlowCookie);

        return Redirect(outcome.Ok
            ? $"{completion}?connected={descriptor.Id}"
            : $"{completion}?provider={descriptor.Id}&error={Uri.EscapeDataString(outcome.Code ?? "oauth_failed")}");
    }

    /// <summary>
    /// The single-page variant, used when the configured redirect URI points at a
    /// dashboard page that posts the code back. Requires the admin's session.
    /// </summary>
    [HttpPost("{provider}/oauth/callback")]
    [HttpPost("oauth/callback")]
    [RequirePermission(Permissions.ProviderConnect)]
    public async Task<IActionResult> OAuthCallback(string? provider, [FromBody] OAuthCallbackRequest req, CancellationToken ct)
    {
        var descriptor = ProviderCatalog.Find(provider ?? ProviderCatalog.Anthropic);
        if (descriptor is null)
            return Error(StatusCodes.Status400BadRequest, "unknown_provider", "Unknown provider.");

        var outcome = await ExchangeAsync(descriptor, req.Code, req.State, requireJwtActor: true, ct);
        if (!outcome.Ok)
            return Error(outcome.Status, outcome.Code!, outcome.Message!);

        return Ok(new { id = outcome.ConnectionId, expiresAt = outcome.ExpiresAt });
    }

    private sealed record ExchangeOutcome(bool Ok, int Status, string? Code, string? Message, Guid? ConnectionId, DateTimeOffset? ExpiresAt);

    /// <summary>
    /// Validates the callback and exchanges the authorization code. The state is
    /// consumed with GETDEL so a replayed callback cannot exchange twice.
    /// </summary>
    private async Task<ExchangeOutcome> ExchangeAsync(
        ProviderDescriptor descriptor, string? code, string? state, bool requireJwtActor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state) || state.Length > 128)
            return new(false, StatusCodes.Status400BadRequest, "invalid_callback", "code and state are required.", null, null);

        var raw = await _redis.GetDatabase().StringGetDeleteAsync(StateKey(state));
        if (raw.IsNullOrEmpty)
            return new(false, StatusCodes.Status400BadRequest, "invalid_state",
                "Login attempt expired or was already used. Start again.", null, null);

        PendingOAuth? pending;
        try { pending = JsonSerializer.Deserialize<PendingOAuth>(raw!); }
        catch (JsonException) { pending = null; }

        if (pending is null || pending.Provider != descriptor.Id)
            return new(false, StatusCodes.Status400BadRequest, "invalid_state", "Login attempt is not valid. Start again.", null, null);

        // The browser that finishes must be the one that started: for the redirect flow
        // that is the flow cookie, for the SPA flow the admin's own session.
        if (requireJwtActor)
        {
            if (pending.OrganizationId != OrgId || pending.ActorId != ActorId)
                return new(false, StatusCodes.Status403Forbidden, "state_mismatch", "This login was started by a different account.", null, null);
        }
        else
        {
            var cookie = Request.Cookies[FlowCookie];
            if (string.IsNullOrEmpty(cookie) || !CryptoEquals(cookie, pending.FlowNonce))
                return new(false, StatusCodes.Status403Forbidden, "flow_mismatch", "This login did not start in this browser. Start again.", null, null);
        }

        var opt = _oauthRegistry.For(descriptor.Id);
        if (opt is null)
            return new(false, StatusCodes.Status409Conflict, "oauth_not_configured", "OAuth is no longer configured for this provider.", null, null);

        OAuthTokenSet tokens;
        try
        {
            tokens = await _oauth.ExchangeCodeAsync(opt, code, pending.CodeVerifier, ct);
        }
        catch (OAuthGrantRejectedException ex)
        {
            await SafeAudit(pending.OrganizationId, AuditActions.OAuthFailed, $"provider={descriptor.Id} reason={ex.Message}", ct);
            return new(false, StatusCodes.Status400BadRequest, "oauth_rejected", "The provider rejected the login. Start again.", null, null);
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning(ex, "OAuth code exchange failed for org {Org}", pending.OrganizationId);
            return new(false, StatusCodes.Status502BadGateway, "oauth_unreachable", "Could not reach the provider. Try again.", null, null);
        }

        var hint = requireJwtActor ? $"OAuth · {ActorEmail}" : $"OAuth · {descriptor.DisplayName}";
        var result = await _connections.CompleteOAuthAsync(
            pending.OrganizationId, descriptor.Id, tokens, hint, null, pending.ActorId, ct);

        await SafeAudit(pending.OrganizationId, AuditActions.ProviderConnected,
            $"id={result.Id} provider={descriptor.Id} type=oauth", ct);

        return new(true, StatusCodes.Status200OK, null, null, result.Id, tokens.ExpiresAt);
    }

    // ----------------------------------------------------------- test / lifecycle

    [HttpPost("{id:guid}/validate")]
    [HttpPost("{id:guid}/test")]
    [RequirePermission(Permissions.ProviderTest)]
    public async Task<IActionResult> Validate(Guid id, CancellationToken ct)
    {
        var (ok, message) = await _connections.ValidateAsync(OrgId, id, ct);
        await _audit.WriteAsync(AuditActions.ProviderValidated, AuditResources.ProviderConnection, id.ToString(),
            after: new { ok }, detail: message, ct: ct);
        var view = await _connections.GetAsync(OrgId, id, ct);
        return Ok(new { ok, message, connection = view });
    }

    [HttpPost("{id:guid}/disconnect")]
    [HttpDelete("{id:guid}")]
    [RequirePermission(Permissions.ProviderDisconnect)]
    public async Task<IActionResult> Disconnect(Guid id, CancellationToken ct)
    {
        var before = await _connections.GetAsync(OrgId, id, ct);
        if (before is null) return NotFound();

        if (!await _connections.DisconnectAsync(OrgId, id, ActorId, ct)) return NotFound();

        await _audit.WriteAsync(AuditActions.ProviderDisconnected, AuditResources.ProviderConnection, id.ToString(),
            before: new { before.Provider, before.Status, before.Hint },
            after: new { status = "revoked" },
            detail: $"provider={before.Provider}", ct: ct);

        // The legacy dashboard expects 204 from DELETE; the new one reads the body.
        return Request.Method == HttpMethods.Delete ? NoContent() : Ok(new { id, status = "revoked" });
    }

    [HttpPost("{id:guid}/enable")]
    [HttpPost("{id:guid}/disable")]
    [RequirePermission(Permissions.ProviderConnect)]
    public async Task<IActionResult> SetEnabled(Guid id, CancellationToken ct)
    {
        var enable = Request.Path.Value?.EndsWith("/enable", StringComparison.OrdinalIgnoreCase) == true;
        try
        {
            if (!await _connections.SetEnabledAsync(OrgId, id, enable, ct)) return NotFound();
        }
        catch (ConnectionStateException ex)
        {
            return Error(StatusCodes.Status409Conflict, ex.Code, ex.Message);
        }

        await _audit.WriteAsync(enable ? AuditActions.ProviderEnabled : AuditActions.ProviderDisabled,
            AuditResources.ProviderConnection, id.ToString(), after: new { enabled = enable }, ct: ct);

        var view = await _connections.GetAsync(OrgId, id, ct);
        return Ok(new { id, status = view?.Status, connection = view });
    }

    // ------------------------------------------------------------------ helpers

    private string Purpose(ConnectProviderRequest req) =>
        string.IsNullOrWhiteSpace(req.ConnectionPurpose) ? "default" : req.ConnectionPurpose.Trim().ToLowerInvariant();

    /// <summary>
    /// Where the browser lands after a redirect-flow connection. Derived from the
    /// configured redirect URI's own origin so it can never become an open redirect.
    /// </summary>
    private string CompletionUrl(string provider)
    {
        var configured = _oauthRegistry.For(provider)?.RedirectUri;
        var path = _oauthRegistry.For(provider)?.CompletionPath;
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/')) path = "/settings/ai-providers";
        return Uri.TryCreate(configured, UriKind.Absolute, out var uri)
            ? $"{uri.Scheme}://{uri.Authority}{path}"
            : path;
    }

    private static string StateKey(string state) => $"oauth:state:{state}";

    /// <summary>Only [a-z0-9-] tokens reach a provider URL, so a region or project cannot inject a path.</summary>
    private static bool IsSafeToken(string value, int maxLength) =>
        value.Length <= maxLength && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static bool CryptoEquals(string a, string b) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));

    private async Task SafeAudit(Guid organizationId, string action, string detail, CancellationToken ct)
    {
        try { await _audit.WriteSystemAsync(organizationId, action, AuditResources.ProviderConnection, null, detail, ct); }
        catch (Exception ex) { _log.LogWarning(ex, "Audit write failed for {Action}", action); }
    }

    private IActionResult Error(int status, string code, string message) =>
        StatusCode(status, new { error = new { code, message } });

    private sealed record PendingOAuth(Guid OrganizationId, Guid ActorId, string CodeVerifier, string Provider, string FlowNonce);
}
