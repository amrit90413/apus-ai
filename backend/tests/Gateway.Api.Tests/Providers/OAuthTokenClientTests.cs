using System.Net;
using System.Text;
using Gateway.Api.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gateway.Api.Tests.Providers;

public sealed class OAuthTokenClientTests
{
    private const string AuthorizeUrl = "https://auth.example.com/oauth/authorize";
    private const string TokenUrl = "https://auth.example.com/oauth/token";
    private const string RedirectUri = "https://ai.example.com/admin/providers/callback";

    private static AnthropicOAuthOptions Options(string? clientSecret = null, string authorizeUrl = AuthorizeUrl) => new()
    {
        ClientId = "client-123",
        ClientSecret = clientSecret,
        AuthorizeUrl = authorizeUrl,
        TokenUrl = TokenUrl,
        RedirectUri = RedirectUri,
        Scopes = "org:read model:invoke",
    };

    private static (OAuthTokenClient Client, StubHandler Handler, FakeHttpClientFactory Factory) Build(
        HttpStatusCode status, string body, AnthropicOAuthOptions? opt = null)
    {
        var handler = new StubHandler(status, body);
        var factory = new FakeHttpClientFactory(handler);
        var client = new OAuthTokenClient(factory, opt ?? Options(), NullLogger<OAuthTokenClient>.Instance);
        return (client, handler, factory);
    }

    /// <summary>Decodes an application/x-www-form-urlencoded body or a URL query string.</summary>
    private static Dictionary<string, string> ParseForm(string encoded) =>
        encoded.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(
                kv => Uri.UnescapeDataString(kv[0].Replace('+', ' ')),
                kv => Uri.UnescapeDataString((kv.Length > 1 ? kv[1] : "").Replace('+', ' ')),
                StringComparer.Ordinal);

    // ---------------------------------------------------------------- BuildAuthorizeUrl

    [Fact]
    public void BuildAuthorizeUrl_includes_every_pkce_parameter()
    {
        var (client, _, _) = Build(HttpStatusCode.OK, "{}");

        var url = client.BuildAuthorizeUrl("state-abc", "challenge-xyz");

        Assert.StartsWith(AuthorizeUrl + "?", url);
        Assert.Contains("redirect_uri=https%3A%2F%2Fai.example.com%2Fadmin%2Fproviders%2Fcallback", url);
        Assert.Contains("code_challenge_method=S256", url);

        var query = ParseForm(new Uri(url).Query.TrimStart('?'));
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("client-123", query["client_id"]);
        Assert.Equal(RedirectUri, query["redirect_uri"]);
        Assert.Equal("org:read model:invoke", query["scope"]);
        Assert.Equal("state-abc", query["state"]);
        Assert.Equal("challenge-xyz", query["code_challenge"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(7, query.Count);
    }

    [Fact]
    public void BuildAuthorizeUrl_appends_with_ampersand_when_the_base_already_has_a_query()
    {
        var opt = Options(authorizeUrl: AuthorizeUrl + "?audience=api");
        var (client, _, _) = Build(HttpStatusCode.OK, "{}", opt);

        var url = client.BuildAuthorizeUrl("s", "c");

        Assert.StartsWith(AuthorizeUrl + "?audience=api&response_type=code", url);
        Assert.Equal(1, url.Count(ch => ch == '?'));

        var query = ParseForm(new Uri(url).Query.TrimStart('?'));
        Assert.Equal("api", query["audience"]);
        Assert.Equal("s", query["state"]);
        Assert.Equal("c", query["code_challenge"]);
    }

    [Fact]
    public void BuildAuthorizeUrl_url_encodes_state_and_challenge()
    {
        var (client, _, _) = Build(HttpStatusCode.OK, "{}");

        var url = client.BuildAuthorizeUrl("a b&c=d", "x/y+z");

        Assert.Contains("state=a%20b%26c%3Dd", url);
        Assert.Contains("code_challenge=x%2Fy%2Bz", url);
    }

    [Fact]
    public void BuildAuthorizeUrl_omits_scope_when_no_scopes_are_configured()
    {
        var opt = Options();
        opt.Scopes = "";
        var (client, _, _) = Build(HttpStatusCode.OK, "{}", opt);

        var url = client.BuildAuthorizeUrl("s", "c");

        Assert.DoesNotContain("scope=", url);
    }

    // ---------------------------------------------------------------- ExchangeCodeAsync

    [Fact]
    public async Task ExchangeCodeAsync_posts_the_authorization_code_grant_and_parses_the_token_set()
    {
        const string body = """{"access_token":"at-1","refresh_token":"rt-1","expires_in":3600,"scope":"org:read model:invoke","token_type":"Bearer"}""";
        var (client, handler, factory) = Build(HttpStatusCode.OK, body);
        var before = DateTimeOffset.UtcNow;

        var tokens = await client.ExchangeCodeAsync("code-42", "verifier-42", CancellationToken.None);

        Assert.Equal(new[] { OAuthTokenClient.HttpClientName }, factory.RequestedNames);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal(new Uri(TokenUrl), handler.RequestUri);
        Assert.Equal("application/x-www-form-urlencoded", handler.ContentType);
        Assert.Contains("application/json", handler.Accept);

        var form = ParseForm(handler.Body!);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("code-42", form["code"]);
        Assert.Equal(RedirectUri, form["redirect_uri"]);
        Assert.Equal("verifier-42", form["code_verifier"]);
        Assert.Equal("client-123", form["client_id"]);
        Assert.False(form.ContainsKey("client_secret"), "public (PKCE-only) clients must not send client_secret");

        Assert.Equal("at-1", tokens.AccessToken);
        Assert.Equal("rt-1", tokens.RefreshToken);
        Assert.Equal("org:read model:invoke", tokens.Scope);
        Assert.NotNull(tokens.ExpiresAt);
        var expected = before.AddSeconds(3600);
        Assert.InRange(tokens.ExpiresAt!.Value, expected.AddSeconds(-5), expected.AddSeconds(5));
    }

    [Fact]
    public async Task ExchangeCodeAsync_sends_client_secret_only_when_configured()
    {
        const string body = """{"access_token":"at-1"}""";
        var (client, handler, _) = Build(HttpStatusCode.OK, body, Options(clientSecret: "shh"));

        await client.ExchangeCodeAsync("code-42", "verifier-42", CancellationToken.None);

        var form = ParseForm(handler.Body!);
        Assert.Equal("shh", form["client_secret"]);
        Assert.Equal("client-123", form["client_id"]);
    }

    [Fact]
    public async Task ExchangeCodeAsync_leaves_optional_fields_null_when_the_provider_omits_them()
    {
        const string body = """{"access_token":"at-only"}""";
        var (client, _, _) = Build(HttpStatusCode.OK, body);

        var tokens = await client.ExchangeCodeAsync("code", "verifier", CancellationToken.None);

        Assert.Equal("at-only", tokens.AccessToken);
        Assert.Null(tokens.RefreshToken);
        Assert.Null(tokens.ExpiresAt);
        Assert.Null(tokens.Scope);
    }

    // ---------------------------------------------------------------- RefreshAsync

    [Fact]
    public async Task RefreshAsync_posts_the_refresh_token_grant()
    {
        const string body = """{"access_token":"at-2","refresh_token":"rt-2","expires_in":600}""";
        var (client, handler, factory) = Build(HttpStatusCode.OK, body);

        var tokens = await client.RefreshAsync("rt-1", CancellationToken.None);

        Assert.Equal(new[] { OAuthTokenClient.HttpClientName }, factory.RequestedNames);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal(new Uri(TokenUrl), handler.RequestUri);

        var form = ParseForm(handler.Body!);
        Assert.Equal("refresh_token", form["grant_type"]);
        Assert.Equal("rt-1", form["refresh_token"]);
        Assert.Equal("client-123", form["client_id"]);
        Assert.False(form.ContainsKey("code"));
        Assert.False(form.ContainsKey("code_verifier"));
        Assert.False(form.ContainsKey("redirect_uri"));

        Assert.Equal("at-2", tokens.AccessToken);
        Assert.Equal("rt-2", tokens.RefreshToken);
        var expected = DateTimeOffset.UtcNow.AddSeconds(600);
        Assert.InRange(tokens.ExpiresAt!.Value, expected.AddSeconds(-5), expected.AddSeconds(5));
    }

    // ---------------------------------------------------------------- error handling

    [Fact]
    public async Task A_400_with_invalid_grant_throws_OAuthGrantRejectedException()
    {
        var (client, _, _) = Build(HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"code expired"}""");

        var ex = await Assert.ThrowsAsync<OAuthGrantRejectedException>(
            () => client.ExchangeCodeAsync("stale", "verifier", CancellationToken.None));

        Assert.Contains("invalid_grant", ex.Message);
    }

    [Fact]
    public async Task A_401_throws_OAuthGrantRejectedException_even_without_a_json_body()
    {
        var (client, _, _) = Build(HttpStatusCode.Unauthorized, "Unauthorized");

        var ex = await Assert.ThrowsAsync<OAuthGrantRejectedException>(
            () => client.RefreshAsync("rt-dead", CancellationToken.None));

        Assert.Contains("401", ex.Message);
    }

    [Fact]
    public async Task A_503_throws_HttpRequestException_with_the_status_code()
    {
        var (client, _, _) = Build(HttpStatusCode.ServiceUnavailable, "try later");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.RefreshAsync("rt-1", CancellationToken.None));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"access_token":""}""")]
    [InlineData("""{"token_type":"Bearer"}""")]
    [InlineData("<html>not json</html>")]
    public async Task A_200_without_an_access_token_throws_HttpRequestException(string body)
    {
        var (client, _, _) = Build(HttpStatusCode.OK, body);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.ExchangeCodeAsync("code", "verifier", CancellationToken.None));
    }

    // ---------------------------------------------------------------- AnthropicOAuthOptions

    [Fact]
    public void Options_are_disabled_by_default()
    {
        Assert.False(new AnthropicOAuthOptions().Enabled);
    }

    [Fact]
    public void Options_are_disabled_when_ClientId_is_empty()
    {
        var opt = Options();
        opt.ClientId = "";
        Assert.False(opt.Enabled);

        opt.ClientId = "   ";
        Assert.False(opt.Enabled);

        opt.ClientId = null;
        Assert.False(opt.Enabled);
    }

    [Fact]
    public void Options_are_enabled_when_client_id_and_all_urls_are_absolute()
    {
        Assert.True(Options().Enabled);
        Assert.True(Options(clientSecret: "secret").Enabled);
    }

    [Theory]
    [InlineData("AuthorizeUrl")]
    [InlineData("TokenUrl")]
    [InlineData("RedirectUri")]
    public void Options_are_disabled_when_any_url_is_relative_or_empty(string property)
    {
        foreach (var bad in new[] { "", "/oauth/callback", "not a url" })
        {
            var opt = Options();
            switch (property)
            {
                case "AuthorizeUrl": opt.AuthorizeUrl = bad; break;
                case "TokenUrl": opt.TokenUrl = bad; break;
                default: opt.RedirectUri = bad; break;
            }
            Assert.False(opt.Enabled, $"{property}=\"{bad}\" should disable OAuth");
        }
    }

    [Fact]
    public void Options_default_refresh_skew_is_two_minutes()
    {
        Assert.Equal(120, new AnthropicOAuthOptions().RefreshSkewSeconds);
    }

    // ---------------------------------------------------------------- test doubles

    /// <summary>Records the outgoing request (before it is disposed by the client) and returns a canned response.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public StubHandler(HttpStatusCode status, string body) { _status = status; _body = body; }

        public int Calls { get; private set; }
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? ContentType { get; private set; }
        public string Accept { get; private set; } = "";
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Method = request.Method;
            RequestUri = request.RequestUri;
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            Accept = string.Join(",", request.Headers.Accept.Select(a => a.MediaType));
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            };
        }
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public FakeHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

        public List<string> RequestedNames { get; } = new();

        public HttpClient CreateClient(string name)
        {
            RequestedNames.Add(name);
            // The client disposes each HttpClient after use; keep the shared handler alive.
            return new HttpClient(_handler, disposeHandler: false);
        }
    }
}
