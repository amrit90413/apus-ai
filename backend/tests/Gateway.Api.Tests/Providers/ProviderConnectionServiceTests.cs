using Gateway.Api.Domain;
using Gateway.Api.Persistence;
using Gateway.Api.Providers;
using Gateway.Api.Security;
using Gateway.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using ConnectionType = Gateway.Api.Domain.ConnectionType;

namespace Gateway.Api.Tests.Providers;

/// <summary>
/// Connection storage against real Postgres: the state machine, the one-live-connection
/// guarantee, secret handling on disconnect, and key rotation.
/// </summary>
public sealed class ProviderConnectionServiceTests : IAsyncLifetime
{
    private readonly PostgresDatabase _db = new();
    private ProviderConnectionService _service = null!;
    private StubValidator _validator = null!;
    private ICredentialEncryption _crypto = null!;

    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();
    private static readonly Guid AdminId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        if (!TestInfra.HasPostgres || !TestInfra.HasRedis) return;

        _crypto = new CredentialEncryption(new FixedKeys(currentVersion: 1));
        _validator = new StubValidator();
        var redis = await ConnectionMultiplexer.ConnectAsync(TestInfra.Redis!);
        var registry = new ProviderOAuthRegistry(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            new AnthropicOAuthOptions(), NullLogger<ProviderOAuthRegistry>.Instance);
        var cache = new MemoryCache(new MemoryCacheOptions());

        _service = new ProviderConnectionService(
            _db.ScopeFactory(), cache, _crypto,
            new OAuthTokenClient(new NullHttpClientFactory(), new AnthropicOAuthOptions(), NullLogger<OAuthTokenClient>.Instance),
            registry, _validator, redis, NullLogger<ProviderConnectionService>.Instance);

        await using var db = _db.NewContext();
        db.Organizations.Add(new Organization { Id = OrgA, Name = "A", Slug = "a-" + Guid.NewGuid().ToString("N")[..6] });
        db.Organizations.Add(new Organization { Id = OrgB, Name = "B", Slug = "b-" + Guid.NewGuid().ToString("N")[..6] });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private static ConnectRequest ApiKey(Guid? org, string provider = "anthropic", string key = "sk-ant-test-0123456789") =>
        new(org, provider, ConnectionType.ApiKey, key, null, null, "Primary", null);

    // ---------------------------------------------------------------- connect

    [InfraFact]
    public async Task Connecting_stores_the_secret_encrypted_and_never_returns_it()
    {
        var result = await _service.ConnectAsync(ApiKey(OrgA), AdminId);

        await using var db = _db.NewContext();
        var row = await db.ProviderCredentials.IgnoreQueryFilters().FirstAsync(c => c.Id == result.Id);

        Assert.DoesNotContain("sk-ant-test", row.EncryptedSecret);
        Assert.Equal("sk-ant-test-0123456789", _crypto.Decrypt(row.EncryptedSecret, row.EncryptionKeyVersion));

        var view = await _service.GetAsync(OrgA, result.Id);
        var serialized = System.Text.Json.JsonSerializer.Serialize(view);
        Assert.DoesNotContain("sk-ant-test", serialized);
        Assert.Contains("6789", serialized);      // only the hint
    }

    [InfraFact]
    public async Task Reconnecting_supersedes_the_previous_connection()
    {
        var first = await _service.ConnectAsync(ApiKey(OrgA, key: "sk-ant-first-000000"), AdminId);
        var second = await _service.ConnectAsync(ApiKey(OrgA, key: "sk-ant-second-11111"), AdminId);

        var connections = await _service.ListAsync(OrgA);
        var live = connections.Where(c => c.Provider == "anthropic" && c.Status is "connected" or "error").ToList();

        Assert.Single(live);
        Assert.Equal(second.Id, live[0].Id);
        Assert.Equal("disabled", connections.First(c => c.Id == first.Id).Status);
    }

    [InfraFact]
    public async Task A_tenant_can_hold_connections_to_several_providers_at_once()
    {
        await _service.ConnectAsync(ApiKey(OrgA, "anthropic"), AdminId);
        await _service.ConnectAsync(ApiKey(OrgA, "openai", "sk-openai-0123456789"), AdminId);

        var providers = await _service.ConnectedProvidersAsync(OrgA);

        Assert.Contains("anthropic", providers);
        Assert.Contains("openai", providers);
    }

    [InfraFact]
    public async Task Bedrock_needs_a_region_and_keeps_the_access_key_id_as_the_hint()
    {
        var request = new ConnectRequest(OrgA, "bedrock", ConnectionType.AwsBedrock,
            "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY",
            new Dictionary<string, string> { ["accessKeyId"] = "AKIAIOSFODNN7EXAMPLE" },
            new Dictionary<string, string> { ["region"] = "us-east-1" },
            "Bedrock", "AKIAIOSFODNN7EXAMPLE");

        var result = await _service.ConnectAsync(request, AdminId);
        var resolved = await _service.ResolveAsync(OrgA, "bedrock");

        Assert.NotNull(resolved);
        Assert.Equal("AKIAIOSFODNN7EXAMPLE", resolved!.Extra1("accessKeyId"));
        Assert.Equal("us-east-1", resolved.Config1("region"));
        Assert.Contains("us-east-1", result.Hint);
        Assert.DoesNotContain("wJalrXUtnFEMI", result.Hint);
    }

    [InfraFact]
    public async Task Connecting_a_provider_that_needs_config_without_it_is_refused()
    {
        var request = new ConnectRequest(OrgA, "bedrock", ConnectionType.AwsBedrock, "secret",
            new Dictionary<string, string> { ["accessKeyId"] = "AKIA" }, null, null, null);

        var ex = await Assert.ThrowsAsync<ConnectionStateException>(() => _service.ConnectAsync(request, AdminId));
        Assert.Equal("missing_config", ex.Code);
    }

    [InfraFact]
    public async Task A_base_url_outside_the_provider_domain_is_refused()
    {
        var request = ApiKey(OrgA) with { Config = new Dictionary<string, string> { ["baseUrl"] = "https://evil.example.com" } };

        var ex = await Assert.ThrowsAsync<ConnectionStateException>(() => _service.ConnectAsync(request, AdminId));
        Assert.Equal("endpoint_not_allowed", ex.Code);
    }

    // ------------------------------------------------------------ tenant scope

    [InfraFact]
    public async Task One_tenant_cannot_see_or_touch_another_tenants_connection()
    {
        var theirs = await _service.ConnectAsync(ApiKey(OrgB, key: "sk-ant-theirs-999999"), AdminId);

        Assert.Null(await _service.GetAsync(OrgA, theirs.Id));
        Assert.DoesNotContain(await _service.ListAsync(OrgA), c => c.Id == theirs.Id);
        Assert.False(await _service.DisconnectAsync(OrgA, theirs.Id, AdminId));

        var (ok, _) = await _service.ValidateAsync(OrgA, theirs.Id);
        Assert.False(ok);

        // And it is still usable by its owner afterwards.
        Assert.NotNull(await _service.GetAsync(OrgB, theirs.Id));
    }

    [InfraFact]
    public async Task Resolving_for_a_tenant_never_returns_another_tenants_credential()
    {
        await _service.ConnectAsync(ApiKey(OrgB, key: "sk-ant-orgb-0000000"), AdminId);

        var resolved = await _service.ResolveAsync(OrgA, "anthropic");

        // OrgA has nothing of its own and there is no platform fallback here.
        Assert.Null(resolved);
    }

    [InfraFact]
    public async Task A_platform_connection_is_the_fallback_for_a_tenant_with_none()
    {
        await _service.ConnectAsync(ApiKey(null, key: "sk-ant-platform-0000"), AdminId);

        var resolved = await _service.ResolveAsync(OrgA, "anthropic");

        Assert.NotNull(resolved);
        Assert.Null(resolved!.OrganizationId);
    }

    [InfraFact]
    public async Task A_tenants_own_connection_wins_over_the_platform_fallback()
    {
        await _service.ConnectAsync(ApiKey(null, key: "sk-ant-platform-1111"), AdminId);
        await _service.ConnectAsync(ApiKey(OrgA, key: "sk-ant-ownkey-22222"), AdminId);

        var resolved = await _service.ResolveAsync(OrgA, "anthropic");

        Assert.Equal(OrgA, resolved!.OrganizationId);
        Assert.Equal("sk-ant-ownkey-22222", resolved.Secret);
    }

    // -------------------------------------------------------------- lifecycle

    [InfraFact]
    public async Task Disconnecting_destroys_the_secret_but_keeps_the_row()
    {
        var connected = await _service.ConnectAsync(ApiKey(OrgA), AdminId);

        Assert.True(await _service.DisconnectAsync(OrgA, connected.Id, AdminId));

        await using var db = _db.NewContext();
        var row = await db.ProviderCredentials.IgnoreQueryFilters().FirstAsync(c => c.Id == connected.Id);

        Assert.Equal(ConnectionStatus.Revoked, row.Status);
        Assert.Equal("", row.EncryptedSecret);
        Assert.Null(row.EncryptedRefreshToken);
        Assert.NotNull(row.RevokedAt);
        Assert.Null(await _service.ResolveAsync(OrgA, "anthropic"));
    }

    [InfraFact]
    public async Task A_disconnected_connection_cannot_be_re_enabled()
    {
        var connected = await _service.ConnectAsync(ApiKey(OrgA), AdminId);
        await _service.DisconnectAsync(OrgA, connected.Id, AdminId);

        var ex = await Assert.ThrowsAsync<ConnectionStateException>(
            () => _service.SetEnabledAsync(OrgA, connected.Id, enabled: true));
        Assert.Equal("connection_revoked", ex.Code);
    }

    [InfraFact]
    public async Task Disabling_stops_the_connection_serving_traffic()
    {
        var connected = await _service.ConnectAsync(ApiKey(OrgA), AdminId);

        await _service.SetEnabledAsync(OrgA, connected.Id, enabled: false);
        _service.Invalidate(OrgA, "anthropic");

        Assert.Null(await _service.ResolveAsync(OrgA, "anthropic"));
    }

    [InfraFact]
    public async Task An_upstream_401_stops_the_connection_serving_and_asks_for_a_reconnect()
    {
        var connected = await _service.ConnectAsync(ApiKey(OrgA), AdminId);

        await _service.ReportFailureAsync(connected.Id, 401, "provider rejected the credential");
        _service.Invalidate(OrgA, "anthropic");

        var view = await _service.GetAsync(OrgA, connected.Id);
        Assert.Equal("reauthentication_required", view!.Status);
        Assert.Equal(1, view.FailureCount);
        Assert.Null(await _service.ResolveAsync(OrgA, "anthropic"));
    }

    [InfraFact]
    public async Task A_transient_upstream_error_keeps_serving_the_connection()
    {
        // A 500 is the provider's problem, not the credential's: blocking every user
        // over one bad minute would be worse than trying again.
        var connected = await _service.ConnectAsync(ApiKey(OrgA), AdminId);

        await _service.ReportFailureAsync(connected.Id, 500, "upstream blew up");
        _service.Invalidate(OrgA, "anthropic");

        Assert.NotNull(await _service.ResolveAsync(OrgA, "anthropic"));
    }

    [InfraFact]
    public async Task A_successful_call_clears_an_earlier_failure()
    {
        var connected = await _service.ConnectAsync(ApiKey(OrgA), AdminId);
        await _service.ReportFailureAsync(connected.Id, 500, "upstream blew up");

        await _service.ReportSuccessAsync(connected.Id);

        var view = await _service.GetAsync(OrgA, connected.Id);
        Assert.Equal(0, view!.FailureCount);
        Assert.Equal("connected", view.Status);
    }

    [InfraFact]
    public async Task Validation_records_the_outcome_on_the_connection()
    {
        var connected = await _service.ConnectAsync(ApiKey(OrgA), AdminId);

        _validator.Result = (false, "Anthropic rejected the credential.");
        var (ok, message) = await _service.ValidateAsync(OrgA, connected.Id);

        Assert.False(ok);
        Assert.Contains("rejected", message);

        var view = await _service.GetAsync(OrgA, connected.Id);
        Assert.Equal("error", view!.Status);
        Assert.NotNull(view.LastValidatedAt);
        Assert.Equal(1, view.FailureCount);
    }

    // ----------------------------------------------------------- key rotation

    [InfraFact]
    public async Task Rotation_re_seals_secrets_under_the_new_key_without_changing_them()
    {
        var connected = await _service.ConnectAsync(ApiKey(OrgA, key: "sk-ant-rotate-4444"), AdminId);

        // A second key version appears; the service now writes under version 2.
        var keys = new FixedKeys(currentVersion: 2);
        var rotatingCrypto = new CredentialEncryption(keys);
        var redis = await ConnectionMultiplexer.ConnectAsync(TestInfra.Redis!);
        var rotating = new ProviderConnectionService(
            _db.ScopeFactory(), new MemoryCache(new MemoryCacheOptions()), rotatingCrypto,
            new OAuthTokenClient(new NullHttpClientFactory(), new AnthropicOAuthOptions(), NullLogger<OAuthTokenClient>.Instance),
            new ProviderOAuthRegistry(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
                new AnthropicOAuthOptions(), NullLogger<ProviderOAuthRegistry>.Instance),
            _validator, redis, NullLogger<ProviderConnectionService>.Instance);

        var rotated = await rotating.RotateEncryptionAsync();
        Assert.True(rotated >= 1);

        await using var db = _db.NewContext();
        var row = await db.ProviderCredentials.IgnoreQueryFilters().FirstAsync(c => c.Id == connected.Id);
        Assert.Equal(2, row.EncryptionKeyVersion);
        Assert.Equal("sk-ant-rotate-4444", rotatingCrypto.Decrypt(row.EncryptedSecret, row.EncryptionKeyVersion));

        // And the connection still resolves and works.
        var resolved = await rotating.ResolveAsync(OrgA, "anthropic");
        Assert.Equal("sk-ant-rotate-4444", resolved!.Secret);
    }

    // ------------------------------------------------------------------ stubs

    private sealed class StubValidator : IProviderValidator
    {
        public (bool ok, string message) Result { get; set; } = (true, "ok");
        public Task<(bool ok, string message)> ProbeAsync(ResolvedConnection connection, CancellationToken ct) =>
            Task.FromResult(Result);
    }

    private sealed class NullHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    /// <summary>Two deterministic keys, so rotation is observable without a KMS.</summary>
    private sealed class FixedKeys : IDataKeyProvider
    {
        private readonly Dictionary<int, byte[]> _keys = new()
        {
            [1] = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray(),
            [2] = Enumerable.Range(0, 32).Select(i => (byte)(255 - i)).ToArray(),
        };

        public FixedKeys(int currentVersion) => CurrentVersion = currentVersion;

        public int CurrentVersion { get; }
        public byte[]? KeyFor(int version) => _keys.TryGetValue(version, out var k) ? k : null;
        public IReadOnlyCollection<int> KnownVersions => _keys.Keys;
    }
}

/// <summary>A fact needing both Postgres and Redis.</summary>
public sealed class InfraFactAttribute : FactAttribute
{
    public InfraFactAttribute()
    {
        if (!TestInfra.HasPostgres) Skip = TestInfra.PostgresSkip;
        else if (!TestInfra.HasRedis) Skip = TestInfra.RedisSkip;
    }
}
