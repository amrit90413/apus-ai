using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Gateway.Api.Domain;
using Gateway.Api.Persistence;
using Gateway.Api.Providers;
using Gateway.Api.Security;
using Gateway.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
// StackExchange.Redis defines Role and ConnectionType too; the domain ones are meant here.
using Role = Gateway.Api.Domain.Role;
using ConnectionType = Gateway.Api.Domain.ConnectionType;

namespace Gateway.Api.Tests.Providers;

/// <summary>
/// The whole browser login, end to end:
///
///   [Connect Anthropic] -> authorize URL -> provider login -> redirect back to
///   /api/provider-connections/anthropic/oauth/callback -> code exchanged for
///   access + refresh tokens -> tokens encrypted -> connection stored -> Connected.
///
/// Plus the ways that flow can be attacked: a replayed callback, a callback that did
/// not start in this browser, a state from a different provider, and a provider that
/// refuses the code.
/// </summary>
public sealed class OAuthConnectionFlowTests : IAsyncLifetime
{
    private const string AuthorizeUrl = "https://claude.ai/oauth/authorize";
    private const string TokenUrl = "https://console.anthropic.com/v1/oauth/token";
    private const string RedirectUri = "https://apus.example.com/api/provider-connections/anthropic/oauth/callback";

    private readonly PostgresDatabase _db = new();
    private ConnectionMultiplexer _redis = null!;
    private IProviderConnectionService _connections = null!;
    private StubTokenHandler _tokenEndpoint = null!;
    private ICredentialEncryption _crypto = null!;

    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid AdminId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        if (!TestInfra.HasPostgres || !TestInfra.HasRedis) return;

        _redis = await ConnectionMultiplexer.ConnectAsync(TestInfra.Redis!);
        _crypto = new CredentialEncryption(new TestKeys());
        _tokenEndpoint = new StubTokenHandler();

        _connections = new ProviderConnectionService(
            _db.ScopeFactory(), new MemoryCache(new MemoryCacheOptions()), _crypto,
            OAuthClient(), Registry(), new AlwaysValid(), _redis,
            NullLogger<ProviderConnectionService>.Instance);

        await using var db = _db.NewContext();
        db.Organizations.Add(new Organization { Id = OrgId, Name = "Acme", Slug = "acme-" + Guid.NewGuid().ToString("N")[..6] });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        if (_redis is not null) await _redis.DisposeAsync();
        await _db.DisposeAsync();
    }

    // ------------------------------------------------------------------ wiring

    private static ProviderOAuthRegistry Registry()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Providers:OAuth:anthropic:ClientId"] = "apus-client-id",
            ["Providers:OAuth:anthropic:ClientSecret"] = "apus-client-secret",
            ["Providers:OAuth:anthropic:AuthorizeUrl"] = AuthorizeUrl,
            ["Providers:OAuth:anthropic:TokenUrl"] = TokenUrl,
            ["Providers:OAuth:anthropic:RedirectUri"] = RedirectUri,
            ["Providers:OAuth:anthropic:Scopes"] = "org:create_api_key user:inference",
            ["Providers:OAuth:anthropic:CompletionPath"] = "/settings/ai-providers",
        }).Build();

        return new ProviderOAuthRegistry(config, new AnthropicOAuthOptions(), NullLogger<ProviderOAuthRegistry>.Instance);
    }

    private OAuthTokenClient OAuthClient() =>
        new(new StubFactory(_tokenEndpoint), new AnthropicOAuthOptions(), NullLogger<OAuthTokenClient>.Instance);

    private ProviderConnectionsController Controller(bool authenticated = true)
    {
        var controller = new ProviderConnectionsController(
            _connections, OAuthClient(), Registry(), _redis,
            new RecordingAudit(), new FeatureFlags(new FeatureFlagOptions()),
            NullLogger<ProviderConnectionsController>.Instance);

        var http = new DefaultHttpContext();
        if (authenticated)
        {
            http.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, AdminId.ToString()),
                new Claim("org_id", OrgId.ToString()),
                new Claim(ClaimTypes.Role, nameof(Role.OrgAdmin)),
                new Claim(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Email, "admin@acme.test"),
            }, "test", ClaimTypes.Name, ClaimTypes.Role));
        }
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    /// <summary>Runs the start step and returns the state plus the flow cookie the browser got.</summary>
    private async Task<(string state, string flowCookie, string authorizeUrl)> StartAsync()
    {
        var controller = Controller();
        var result = await controller.OAuthStart("anthropic", redirect: false, default);

        var payload = Assert.IsType<OkObjectResult>(result).Value!;
        var url = (string)payload.GetType().GetProperty("authorizeUrl")!.GetValue(payload)!;
        var state = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query)["state"]!;

        var setCookie = controller.Response.Headers.SetCookie.ToString();
        var cookie = setCookie.Split(';')[0].Split('=', 2)[1];
        return (state, cookie, url);
    }

    // ------------------------------------------------------------------- start

    [InfraFact]
    public async Task Start_sends_the_admin_to_the_provider_with_pkce_and_state()
    {
        var (state, cookie, url) = await StartAsync();
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);

        Assert.StartsWith(AuthorizeUrl, url);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("apus-client-id", query["client_id"]);
        Assert.Equal(RedirectUri, query["redirect_uri"]);
        Assert.Equal("org:create_api_key user:inference", query["scope"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.False(string.IsNullOrWhiteSpace(query["code_challenge"]));
        Assert.False(string.IsNullOrWhiteSpace(state));
        Assert.False(string.IsNullOrWhiteSpace(cookie));
    }

    [InfraFact]
    public async Task The_flow_cookie_is_http_only_and_lax_and_secure_over_https()
    {
        var controller = Controller();
        controller.Request.Scheme = "https";   // what UseForwardedHeaders yields in production
        await controller.OAuthStart("anthropic", redirect: false, default);

        var setCookie = controller.Response.Headers.SetCookie.ToString();

        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        // Lax, not Strict: the cookie has to survive the provider's top-level redirect back.
        Assert.Contains("samesite=lax", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [InfraFact]
    public async Task The_flow_still_works_on_a_plain_http_local_instance()
    {
        // Secure cookies are dropped by the browser over HTTP, which would make every
        // local callback fail the flow check.
        var controller = Controller();
        await controller.OAuthStart("anthropic", redirect: false, default);

        var setCookie = controller.Response.Headers.SetCookie.ToString();

        Assert.DoesNotContain("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [InfraFact]
    public async Task Start_can_redirect_the_browser_directly_so_connect_can_be_a_link()
    {
        var controller = Controller();

        var result = await controller.OAuthStart("anthropic", redirect: true, default);

        Assert.StartsWith(AuthorizeUrl, Assert.IsType<RedirectResult>(result).Url);
    }

    [InfraFact]
    public async Task A_provider_with_no_oauth_client_says_so_instead_of_half_starting()
    {
        var controller = Controller();

        var result = await controller.OAuthStart("openai", redirect: false, default);

        // OpenAI supports no third-party OAuth at all, so this is a 400 rather than a
        // 409 "not configured here".
        Assert.Equal(400, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    // ---------------------------------------------------------------- callback

    [InfraFact]
    public async Task The_callback_exchanges_the_code_and_stores_encrypted_tokens()
    {
        var (state, cookie, _) = await StartAsync();
        _tokenEndpoint.Respond(HttpStatusCode.OK, """
            {"access_token":"at-live-abc123","refresh_token":"rt-live-xyz789",
             "expires_in":3600,"scope":"org:create_api_key user:inference"}
            """);

        var controller = Controller(authenticated: false);
        controller.Request.Headers.Cookie = $"apus_oauth_flow={cookie}";

        var result = await controller.OAuthCallbackRedirect("anthropic", "auth-code-1", state, null, default);

        // The admin lands back on the providers page with a success marker.
        Assert.Equal("https://apus.example.com/settings/ai-providers?connected=anthropic",
            Assert.IsType<RedirectResult>(result).Url);

        // The exchange used the authorization_code grant with PKCE and the client secret.
        var form = _tokenEndpoint.LastForm!;
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("auth-code-1", form["code"]);
        Assert.Equal(RedirectUri, form["redirect_uri"]);
        Assert.Equal("apus-client-id", form["client_id"]);
        Assert.Equal("apus-client-secret", form["client_secret"]);
        Assert.False(string.IsNullOrWhiteSpace(form["code_verifier"]));

        // Both tokens are stored encrypted, and neither appears anywhere in plaintext.
        await using var db = _db.NewContext();
        var row = await db.ProviderCredentials.IgnoreQueryFilters()
            .OrderByDescending(c => c.CreatedAt).FirstAsync(c => c.OrganizationId == OrgId);

        Assert.Equal(ConnectionStatus.Connected, row.Status);
        Assert.Equal(ConnectionType.OAuth, row.ConnectionType);
        Assert.DoesNotContain("at-live-abc123", row.EncryptedSecret);
        Assert.DoesNotContain("rt-live-xyz789", row.EncryptedRefreshToken!);
        Assert.Equal("at-live-abc123", _crypto.Decrypt(row.EncryptedSecret, row.EncryptionKeyVersion));
        Assert.Equal("rt-live-xyz789", _crypto.Decrypt(row.EncryptedRefreshToken!, row.EncryptionKeyVersion));
        Assert.Equal("org:create_api_key user:inference", row.Scopes);
        Assert.NotNull(row.AccessExpiresAt);
        Assert.True(row.AccessExpiresAt > DateTimeOffset.UtcNow.AddMinutes(50));
        Assert.Equal(AdminId, row.CreatedBy);
    }

    [InfraFact]
    public async Task After_the_callback_the_connection_resolves_as_a_bearer_credential()
    {
        var (state, cookie, _) = await StartAsync();
        _tokenEndpoint.Respond(HttpStatusCode.OK, """{"access_token":"at-usable","refresh_token":"rt-usable","expires_in":3600}""");

        var controller = Controller(authenticated: false);
        controller.Request.Headers.Cookie = $"apus_oauth_flow={cookie}";
        await controller.OAuthCallbackRedirect("anthropic", "code", state, null, default);

        var resolved = await _connections.ResolveAsync(OrgId, "anthropic");

        Assert.NotNull(resolved);
        Assert.Equal(ConnectionType.OAuth, resolved!.Type);
        Assert.Equal("at-usable", resolved.Secret);
        Assert.Equal(AuthScheme.Bearer, ProviderEndpoints.SchemeFor(resolved));

        // And the tenant now counts Anthropic as connected.
        Assert.Contains("anthropic", await _connections.ConnectedProvidersAsync(OrgId));
    }

    [InfraFact]
    public async Task The_json_callback_used_by_the_dashboard_works_the_same_way()
    {
        var controller = Controller();
        var start = await controller.OAuthStart("anthropic", redirect: false, default);
        var payload = Assert.IsType<OkObjectResult>(start).Value!;
        var url = (string)payload.GetType().GetProperty("authorizeUrl")!.GetValue(payload)!;
        var state = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query)["state"]!;

        _tokenEndpoint.Respond(HttpStatusCode.OK, """{"access_token":"at-spa","refresh_token":"rt-spa","expires_in":1800}""");

        // Same authenticated admin finishes it, as the SPA flow does.
        var result = await controller.OAuthCallback("anthropic", new OAuthCallbackRequest("code-spa", state), default);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("at-spa", (await _connections.ResolveAsync(OrgId, "anthropic"))!.Secret);
    }

    // ------------------------------------------------------------- the attacks

    [InfraFact]
    public async Task A_replayed_callback_cannot_exchange_the_code_twice()
    {
        var (state, cookie, _) = await StartAsync();
        _tokenEndpoint.Respond(HttpStatusCode.OK, """{"access_token":"at-1","refresh_token":"rt-1","expires_in":3600}""");

        var first = Controller(authenticated: false);
        first.Request.Headers.Cookie = $"apus_oauth_flow={cookie}";
        await first.OAuthCallbackRedirect("anthropic", "code", state, null, default);

        var replay = Controller(authenticated: false);
        replay.Request.Headers.Cookie = $"apus_oauth_flow={cookie}";
        var result = await replay.OAuthCallbackRedirect("anthropic", "code", state, null, default);

        Assert.Contains("error=invalid_state", Assert.IsType<RedirectResult>(result).Url);
        Assert.Equal(1, _tokenEndpoint.Calls);   // the provider was only called once
    }

    [InfraFact]
    public async Task A_callback_without_the_flow_cookie_is_refused()
    {
        // Login CSRF: an attacker who can make the admin's browser hit the callback
        // with their own code would otherwise attach their provider account to this tenant.
        var (state, _, _) = await StartAsync();
        _tokenEndpoint.Respond(HttpStatusCode.OK, """{"access_token":"at-evil","expires_in":3600}""");

        var controller = Controller(authenticated: false);
        var result = await controller.OAuthCallbackRedirect("anthropic", "attacker-code", state, null, default);

        Assert.Contains("error=flow_mismatch", Assert.IsType<RedirectResult>(result).Url);
        Assert.Equal(0, _tokenEndpoint.Calls);
    }

    [InfraFact]
    public async Task A_callback_with_the_wrong_flow_cookie_is_refused()
    {
        var (state, _, _) = await StartAsync();

        var controller = Controller(authenticated: false);
        controller.Request.Headers.Cookie = "apus_oauth_flow=some-other-value";
        var result = await controller.OAuthCallbackRedirect("anthropic", "code", state, null, default);

        Assert.Contains("error=flow_mismatch", Assert.IsType<RedirectResult>(result).Url);
    }

    [InfraFact]
    public async Task A_state_issued_for_one_provider_cannot_complete_another()
    {
        var (state, cookie, _) = await StartAsync();

        var controller = Controller(authenticated: false);
        controller.Request.Headers.Cookie = $"apus_oauth_flow={cookie}";
        var result = await controller.OAuthCallbackRedirect("gemini", "code", state, null, default);

        Assert.Contains("error=invalid_state", Assert.IsType<RedirectResult>(result).Url);
    }

    [InfraFact]
    public async Task An_unknown_state_is_refused_without_calling_the_provider()
    {
        var controller = Controller(authenticated: false);
        controller.Request.Headers.Cookie = "apus_oauth_flow=whatever";

        var result = await controller.OAuthCallbackRedirect("anthropic", "code", "never-issued", null, default);

        Assert.Contains("error=invalid_state", Assert.IsType<RedirectResult>(result).Url);
        Assert.Equal(0, _tokenEndpoint.Calls);
    }

    [InfraFact]
    public async Task A_provider_that_rejects_the_code_leaves_nothing_connected()
    {
        var (state, cookie, _) = await StartAsync();
        _tokenEndpoint.Respond(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");

        var controller = Controller(authenticated: false);
        controller.Request.Headers.Cookie = $"apus_oauth_flow={cookie}";
        var result = await controller.OAuthCallbackRedirect("anthropic", "stale-code", state, null, default);

        Assert.Contains("error=oauth_rejected", Assert.IsType<RedirectResult>(result).Url);
        Assert.Null(await _connections.ResolveAsync(OrgId, "anthropic"));
    }

    [InfraFact]
    public async Task An_admin_who_declines_at_the_provider_is_returned_with_a_reason()
    {
        var controller = Controller(authenticated: false);

        var result = await controller.OAuthCallbackRedirect("anthropic", null, null, "access_denied", default);

        Assert.Contains("error=provider_denied", Assert.IsType<RedirectResult>(result).Url);
    }

    [InfraFact]
    public async Task The_completion_redirect_stays_on_the_configured_origin()
    {
        // An open redirect here would be handed a browser that has just authenticated.
        var (state, cookie, _) = await StartAsync();
        _tokenEndpoint.Respond(HttpStatusCode.OK, """{"access_token":"at","expires_in":60}""");

        var controller = Controller(authenticated: false);
        controller.Request.Headers.Cookie = $"apus_oauth_flow={cookie}";
        var result = await controller.OAuthCallbackRedirect("anthropic", "code", state, null, default);

        Assert.StartsWith("https://apus.example.com/", Assert.IsType<RedirectResult>(result).Url);
    }

    [InfraFact]
    public async Task Reconnecting_replaces_the_previous_oauth_connection()
    {
        async Task ConnectAsync(string accessToken)
        {
            var (state, cookie, _) = await StartAsync();
            _tokenEndpoint.Respond(HttpStatusCode.OK,
                $$"""{"access_token":"{{accessToken}}","refresh_token":"rt","expires_in":3600}""");
            var controller = Controller(authenticated: false);
            controller.Request.Headers.Cookie = $"apus_oauth_flow={cookie}";
            await controller.OAuthCallbackRedirect("anthropic", "code", state, null, default);
        }

        await ConnectAsync("at-first");
        await ConnectAsync("at-second");

        var live = (await _connections.ListAsync(OrgId))
            .Where(c => c.Provider == "anthropic" && c.Status == "connected")
            .ToList();

        Assert.Single(live);
        _connections.Invalidate(OrgId, "anthropic");
        Assert.Equal("at-second", (await _connections.ResolveAsync(OrgId, "anthropic"))!.Secret);
    }

    // ------------------------------------------------------------------ stubs

    private sealed class StubTokenHandler : HttpMessageHandler
    {
        private HttpStatusCode _status = HttpStatusCode.OK;
        private string _body = "{}";

        public int Calls { get; private set; }
        public Dictionary<string, string>? LastForm { get; private set; }

        public void Respond(HttpStatusCode status, string body) { _status = status; _body = body; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var raw = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            LastForm = raw.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split('=', 2))
                .ToDictionary(
                    kv => Uri.UnescapeDataString(kv[0].Replace('+', ' ')),
                    kv => Uri.UnescapeDataString((kv.Length > 1 ? kv[1] : "").Replace('+', ' ')));

            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class AlwaysValid : IProviderValidator
    {
        public Task<(bool ok, string message)> ProbeAsync(ResolvedConnection connection, CancellationToken ct) =>
            Task.FromResult((true, "ok"));
    }

    private sealed class RecordingAudit : IAuditWriter
    {
        public void Record(string action, string resourceType, string? resourceId, object? before = null, object? after = null, string? detail = null) { }
        public Task WriteAsync(string action, string resourceType, string? resourceId, object? before = null, object? after = null, string? detail = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task WriteSystemAsync(Guid organizationId, string action, string resourceType, string? resourceId, string? detail = null, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class TestKeys : IDataKeyProvider
    {
        private readonly byte[] _key = Enumerable.Range(0, 32).Select(i => (byte)(i * 7)).ToArray();
        public int CurrentVersion => 1;
        public byte[]? KeyFor(int version) => version == 1 ? _key : null;
        public IReadOnlyCollection<int> KnownVersions => new[] { 1 };
    }
}
