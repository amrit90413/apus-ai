using System.Security.Claims;
using System.Text.Json.Nodes;
using Gateway.Api.Domain;
using Gateway.Api.Gateway;
using Gateway.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Gateway.Api.Tests.Security;

public sealed class CredentialEncryptionTests
{
    private static readonly byte[] KeyOne = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] KeyTwo = Enumerable.Range(0, 32).Select(i => (byte)(200 - i)).ToArray();

    private sealed class Keys : IDataKeyProvider
    {
        private readonly Dictionary<int, byte[]> _keys;
        public Keys(int current, params (int version, byte[] key)[] keys)
        {
            CurrentVersion = current;
            _keys = keys.ToDictionary(k => k.version, k => k.key);
        }
        public int CurrentVersion { get; }
        public byte[]? KeyFor(int version) => _keys.TryGetValue(version, out var k) ? k : null;
        public IReadOnlyCollection<int> KnownVersions => _keys.Keys;
    }

    [Fact]
    public void Round_trips_under_the_current_key()
    {
        var crypto = new CredentialEncryption(new Keys(1, (1, KeyOne)));

        var sealedSecret = crypto.Encrypt("sk-ant-secret");

        Assert.Equal(1, sealedSecret.KeyVersion);
        Assert.DoesNotContain("sk-ant", sealedSecret.Ciphertext);
        Assert.Equal("sk-ant-secret", crypto.Decrypt(sealedSecret.Ciphertext, 1));
    }

    [Fact]
    public void Writes_under_the_current_version_while_still_reading_older_ones()
    {
        var v1 = new CredentialEncryption(new Keys(1, (1, KeyOne)));
        var old = v1.Encrypt("old-secret");

        var v2 = new CredentialEncryption(new Keys(2, (1, KeyOne), (2, KeyTwo)));

        Assert.Equal("old-secret", v2.Decrypt(old.Ciphertext, 1));   // still readable
        Assert.Equal(2, v2.Encrypt("new-secret").KeyVersion);        // new writes move on
    }

    [Fact]
    public void Rotate_re_seals_without_changing_the_value()
    {
        var v1 = new CredentialEncryption(new Keys(1, (1, KeyOne)));
        var old = v1.Encrypt("rotate-me");

        var v2 = new CredentialEncryption(new Keys(2, (1, KeyOne), (2, KeyTwo)));
        var rotated = v2.Rotate(old.Ciphertext, 1)!.Value;

        Assert.Equal(2, rotated.KeyVersion);
        Assert.NotEqual(old.Ciphertext, rotated.Ciphertext);
        Assert.Equal("rotate-me", v2.Decrypt(rotated.Ciphertext, 2));
    }

    [Fact]
    public void Rotate_is_a_no_op_for_a_value_already_on_the_current_key()
    {
        var crypto = new CredentialEncryption(new Keys(1, (1, KeyOne)));
        var current = crypto.Encrypt("fine");

        Assert.Null(crypto.Rotate(current.Ciphertext, 1));
    }

    [Fact]
    public void A_retired_key_version_fails_loudly_rather_than_returning_rubbish()
    {
        var crypto = new CredentialEncryption(new Keys(2, (2, KeyTwo)));

        var ex = Assert.Throws<System.Security.Cryptography.CryptographicException>(
            () => crypto.Decrypt("Zm9vYmFy", 1));

        Assert.Contains("key version 1", ex.Message);
    }

    [Fact]
    public void The_same_plaintext_encrypts_differently_every_time()
    {
        var crypto = new CredentialEncryption(new Keys(1, (1, KeyOne)));

        Assert.NotEqual(crypto.Encrypt("same").Ciphertext, crypto.Encrypt("same").Ciphertext);
    }

    [Fact]
    public void A_tampered_ciphertext_does_not_decrypt()
    {
        var crypto = new CredentialEncryption(new Keys(1, (1, KeyOne)));
        var sealedSecret = crypto.Encrypt("sk-ant-secret");

        var bytes = Convert.FromBase64String(sealedSecret.Ciphertext);
        bytes[^1] ^= 0xFF;

        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(
            () => crypto.Decrypt(Convert.ToBase64String(bytes), 1));
    }
}

public sealed class AuditRedactionTests
{
    [Fact]
    public void Fields_that_look_like_secrets_are_replaced()
    {
        var json = AuditWriter.Redact(new
        {
            provider = "anthropic",
            apiKey = "sk-ant-verysecret",
            refreshToken = "rt-secret",
            region = "us-east-1",
        })!;

        Assert.DoesNotContain("sk-ant-verysecret", json);
        Assert.DoesNotContain("rt-secret", json);
        Assert.Contains("[redacted]", json);
        Assert.Contains("us-east-1", json);     // harmless config survives
    }

    [Fact]
    public void Redaction_reaches_into_nested_objects_and_arrays()
    {
        var json = AuditWriter.Redact(new
        {
            connections = new[]
            {
                new { name = "primary", credential = new { secretAccessKey = "wJalrXUtn" } },
            },
        })!;

        Assert.DoesNotContain("wJalrXUtn", json);
        Assert.Contains("primary", json);
    }

    [Theory]
    [InlineData("password")]
    [InlineData("client_secret")]
    [InlineData("Authorization")]
    [InlineData("privateKey")]
    [InlineData("assertion")]
    public void Every_known_secret_field_name_is_caught(string field)
    {
        var node = new JsonObject { [field] = "leak-me" };

        Assert.DoesNotContain("leak-me", AuditWriter.Redact(node)!);
    }

    [Fact]
    public void Null_stays_null_so_an_unchanged_side_is_not_recorded_as_empty()
    {
        Assert.Null(AuditWriter.Redact(null));
    }
}

public sealed class PermissionsTests
{
    [Fact]
    public void A_plain_member_can_only_see_their_own_usage()
    {
        Assert.True(RolePermissions.Has(Role.User, Permissions.UsageViewSelf));
        Assert.False(RolePermissions.Has(Role.User, Permissions.UsageViewTenant));
        Assert.False(RolePermissions.Has(Role.User, Permissions.ProviderConnect));
        Assert.False(RolePermissions.Has(Role.User, Permissions.AllowanceUpdate));
    }

    [Fact]
    public void A_viewer_reads_everything_and_changes_nothing()
    {
        Assert.True(RolePermissions.Has(Role.Viewer, Permissions.UsageViewTenant));
        Assert.True(RolePermissions.Has(Role.Viewer, Permissions.ProviderView));
        Assert.False(RolePermissions.Has(Role.Viewer, Permissions.ProviderConnect));
        Assert.False(RolePermissions.Has(Role.Viewer, Permissions.AllowanceUpdate));
    }

    [Fact]
    public void An_ai_admin_runs_providers_and_allowances_but_not_billing()
    {
        Assert.True(RolePermissions.Has(Role.AiAdmin, Permissions.ProviderConnect));
        Assert.True(RolePermissions.Has(Role.AiAdmin, Permissions.ProviderDisconnect));
        Assert.True(RolePermissions.Has(Role.AiAdmin, Permissions.AllowanceUpdate));
        Assert.False(RolePermissions.Has(Role.AiAdmin, Permissions.BillingManage));
    }

    [Fact]
    public void A_billing_admin_is_the_mirror_image()
    {
        Assert.True(RolePermissions.Has(Role.BillingAdmin, Permissions.BillingManage));
        Assert.True(RolePermissions.Has(Role.BillingAdmin, Permissions.AllowanceUpdate));
        Assert.False(RolePermissions.Has(Role.BillingAdmin, Permissions.ProviderConnect));
        Assert.False(RolePermissions.Has(Role.BillingAdmin, Permissions.AiUserCreate));
    }

    [Fact]
    public void Only_the_platform_role_holds_the_platform_permission()
    {
        Assert.True(RolePermissions.Has(Role.SuperAdmin, Permissions.PlatformAdmin));
        Assert.False(RolePermissions.Has(Role.OrgAdmin, Permissions.PlatformAdmin));
    }

    [Fact]
    public void An_org_admin_can_do_everything_within_the_tenant()
    {
        foreach (var permission in Permissions.All.Where(p => p != Permissions.PlatformAdmin))
            Assert.True(RolePermissions.Has(Role.OrgAdmin, permission), $"OrgAdmin should hold {permission}");
    }

    [Fact]
    public async Task The_policy_provider_builds_a_policy_for_a_real_permission()
    {
        var provider = new PermissionPolicyProvider(Options.Create(new AuthorizationOptions()));

        var policy = await provider.GetPolicyAsync(PermissionPolicyProvider.Prefix + Permissions.ProviderConnect);

        Assert.NotNull(policy);
        Assert.Contains(policy!.Requirements, r => r is PermissionRequirement { Permission: "Provider.Connect" });
    }

    [Fact]
    public async Task An_unknown_permission_yields_no_policy_so_the_request_is_refused()
    {
        var provider = new PermissionPolicyProvider(Options.Create(new AuthorizationOptions()));

        Assert.Null(await provider.GetPolicyAsync(PermissionPolicyProvider.Prefix + "Made.Up"));
    }

    [Fact]
    public async Task The_handler_grants_only_what_the_role_actually_holds()
    {
        var requirement = new PermissionRequirement(Permissions.ProviderConnect);
        var handler = new PermissionHandler();

        var admin = Principal(Role.OrgAdmin);
        var adminContext = new AuthorizationHandlerContext(new[] { requirement }, admin, null);
        await handler.HandleAsync(adminContext);
        Assert.True(adminContext.HasSucceeded);

        var member = Principal(Role.User);
        var memberContext = new AuthorizationHandlerContext(new[] { requirement }, member, null);
        await handler.HandleAsync(memberContext);
        Assert.False(memberContext.HasSucceeded);
    }

    [Fact]
    public void Role_authority_is_compared_by_tier_not_by_enum_ordinal()
    {
        // AiAdmin and BillingAdmin have higher ordinals than OrgAdmin but less
        // authority; a bare >= comparison would silently promote them.
        Assert.True(RoleTiers.IsOrgAdmin(Role.OrgAdmin));
        Assert.True(RoleTiers.IsOrgAdmin(Role.SuperAdmin));
        Assert.False(RoleTiers.IsOrgAdmin(Role.AiAdmin));
        Assert.False(RoleTiers.IsOrgAdmin(Role.BillingAdmin));
        Assert.False(RoleTiers.IsOrgAdmin(Role.Viewer));
        Assert.False(RoleTiers.IsOrgAdmin(Role.User));
    }

    private static ClaimsPrincipal Principal(Role role) =>
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Role, role.ToString()) }, "test", ClaimTypes.Name, ClaimTypes.Role));
}

public sealed class FeatureFlagTests
{
    [Fact]
    public void An_unconfigured_flag_falls_back_to_its_default()
    {
        var flags = new FeatureFlags(new FeatureFlagOptions());

        Assert.True(flags.IsEnabled(FeatureFlagNames.ProviderConnections));
        Assert.False(flags.IsEnabled(FeatureFlagNames.ProviderFallback));
    }

    [Fact]
    public void A_flag_can_be_switched_on_for_named_tenants_first()
    {
        var pilot = Guid.NewGuid();
        var other = Guid.NewGuid();
        var flags = new FeatureFlags(new FeatureFlagOptions
        {
            Flags =
            {
                [FeatureFlagNames.ProviderFallback] = new FeatureFlagSetting
                {
                    Enabled = false,
                    Organizations = { pilot.ToString() },
                },
            },
        });

        Assert.True(flags.IsEnabled(FeatureFlagNames.ProviderFallback, pilot));
        Assert.False(flags.IsEnabled(FeatureFlagNames.ProviderFallback, other));
        Assert.False(flags.IsEnabled(FeatureFlagNames.ProviderFallback));
    }

    [Fact]
    public void Enabling_a_flag_globally_covers_every_tenant()
    {
        var flags = new FeatureFlags(new FeatureFlagOptions
        {
            Flags = { [FeatureFlagNames.ProviderFallback] = new FeatureFlagSetting { Enabled = true } },
        });

        Assert.True(flags.IsEnabled(FeatureFlagNames.ProviderFallback, Guid.NewGuid()));
    }

    [Fact]
    public void The_snapshot_covers_every_known_flag()
    {
        var snapshot = new FeatureFlags(new FeatureFlagOptions()).Snapshot();

        Assert.Contains(FeatureFlagNames.ProviderConnections, snapshot.Keys);
        Assert.Contains(FeatureFlagNames.ChildAllowances, snapshot.Keys);
        Assert.Contains(FeatureFlagNames.UsageBilling, snapshot.Keys);
    }
}

public sealed class GatewayErrorCodeTests
{
    [Theory]
    [InlineData(GatewayErrorCodes.ModelNotAllowed, 403)]
    [InlineData(GatewayErrorCodes.ProviderNotAllowed, 403)]
    [InlineData(GatewayErrorCodes.UserAiAccessDisabled, 403)]
    [InlineData(GatewayErrorCodes.UserAllowanceExceeded, 402)]
    [InlineData(GatewayErrorCodes.TenantAllowanceExceeded, 402)]
    [InlineData(GatewayErrorCodes.SubscriptionInactive, 402)]
    [InlineData(GatewayErrorCodes.RateLimitExceeded, 429)]
    [InlineData(GatewayErrorCodes.QuotaExceeded, 429)]
    [InlineData(GatewayErrorCodes.ProviderNotConnected, 503)]
    [InlineData(GatewayErrorCodes.ProviderUnavailable, 502)]
    [InlineData(GatewayErrorCodes.InvalidRequest, 400)]
    public void Codes_map_to_the_status_a_client_should_act_on(string code, int expected) =>
        Assert.Equal(expected, GatewayErrorCodes.Http(code).status);

    [Fact]
    public void An_exhausted_allowance_is_not_retryable_so_it_is_not_a_429()
    {
        // A client that retries a 429 forever would burn its own rate limit and never
        // recover; only an admin top-up fixes this.
        Assert.Equal(402, GatewayErrorCodes.Http(GatewayErrorCodes.UserAllowanceExceeded).status);
    }

    [Fact]
    public void Error_types_follow_the_anthropic_vocabulary_clients_already_handle()
    {
        Assert.Equal("permission_error", GatewayErrorCodes.Http(GatewayErrorCodes.ModelNotAllowed).type);
        Assert.Equal("rate_limit_error", GatewayErrorCodes.Http(GatewayErrorCodes.RateLimitExceeded).type);
        Assert.Equal("invalid_request_error", GatewayErrorCodes.Http(GatewayErrorCodes.InvalidRequest).type);
    }

    [Fact]
    public void An_unrecognised_code_fails_closed_as_a_server_error()
    {
        Assert.Equal(500, GatewayErrorCodes.Http("SOMETHING_NEW").status);
    }
}

public sealed class PolicyNarrowingTests
{
    [Fact]
    public void A_layer_that_says_nothing_restricts_nothing()
    {
        var result = QuotaPolicyResolver.Narrow(null, Array.Empty<string>(), new[] { "a", "b" });

        Assert.Equal(new[] { "a", "b" }, result);
    }

    [Fact]
    public void Layers_intersect_so_the_most_restrictive_wins()
    {
        var result = QuotaPolicyResolver.Narrow(
            new[] { "claude-opus-5", "claude-sonnet-5", "gpt-4o" },   // platform
            new[] { "claude-opus-5", "claude-sonnet-5" },             // tenant
            new[] { "claude-sonnet-5" });                             // workspace

        Assert.Equal(new[] { "claude-sonnet-5" }, result);
    }

    [Fact]
    public void A_lower_layer_cannot_widen_a_higher_one()
    {
        // A tenant admin granting a model the platform forbids must get nothing.
        var result = QuotaPolicyResolver.Narrow(
            new[] { "claude-sonnet-5" },
            new[] { "claude-opus-5" });

        Assert.Empty(result);
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        var result = QuotaPolicyResolver.Narrow(new[] { "Claude-Sonnet-5" }, new[] { "claude-sonnet-5" });

        Assert.Single(result);
    }

    [Fact]
    public void Nothing_configured_anywhere_means_no_restriction()
    {
        Assert.Empty(QuotaPolicyResolver.Narrow(null, null, null));
    }
}
