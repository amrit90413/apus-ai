using Gateway.Api.Quota;
using Gateway.Api.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace Gateway.Api.Tests.Quota;

/// <summary>
/// The hierarchical rate limiter against real Redis. Its guarantee — that several
/// gateway replicas share one set of counters and evaluate the whole hierarchy
/// atomically — is exactly what an in-memory fake would paper over.
/// </summary>
public sealed class RateLimiterTests : IAsyncLifetime
{
    private ConnectionMultiplexer _redis = null!;
    private RateLimiter _limiter = null!;
    private string _prefix = "";

    public async Task InitializeAsync()
    {
        if (!TestInfra.HasRedis) return;

        _redis = await ConnectionMultiplexer.ConnectAsync(TestInfra.Redis!);
        _limiter = new RateLimiter(_redis, NullLogger<RateLimiter>.Instance);
        await _limiter.LoadScriptsAsync(ScriptDirectory());
        _prefix = "test:" + Guid.NewGuid().ToString("N")[..8] + ":";
    }

    public async Task DisposeAsync()
    {
        if (_redis is not null) await _redis.DisposeAsync();
    }

    private static string ScriptDirectory() => Path.Combine(AppContext.BaseDirectory, "Quota");

    private RateRule Rule(string scope, long limit, long increment = 1, int window = 60) =>
        new(scope, _prefix + scope, limit, window, increment);

    [RedisFact]
    public async Task Requests_are_allowed_up_to_the_limit_and_then_refused()
    {
        var rules = new[] { Rule(RateLimiter.UserScope, 3) };

        for (var i = 0; i < 3; i++)
            Assert.True((await _limiter.CheckAsync(rules)).Allowed, $"request {i + 1} should be allowed");

        var refused = await _limiter.CheckAsync(rules);

        Assert.False(refused.Allowed);
        Assert.Equal(RateLimiter.UserScope, refused.ViolatedScope);
        Assert.True(refused.RetryAfterSeconds > 0);
    }

    [RedisFact]
    public async Task The_tightest_level_in_the_hierarchy_is_the_one_that_blocks()
    {
        var rules = new[]
        {
            Rule(RateLimiter.PlatformScope, 1000),
            Rule(RateLimiter.TenantScope, 100),
            Rule(RateLimiter.UserScope, 1),
        };

        Assert.True((await _limiter.CheckAsync(rules)).Allowed);
        var refused = await _limiter.CheckAsync(rules);

        Assert.False(refused.Allowed);
        Assert.Equal(RateLimiter.UserScope, refused.ViolatedScope);
    }

    [RedisFact]
    public async Task A_refused_request_leaves_every_counter_untouched()
    {
        // Otherwise a user blocked at one level would still burn the tenant's budget.
        var rules = new[] { Rule(RateLimiter.TenantScope, 100), Rule(RateLimiter.UserScope, 1) };

        await _limiter.CheckAsync(rules);
        var refused = await _limiter.CheckAsync(rules);
        Assert.False(refused.Allowed);

        var tenant = refused.States.Single(s => s.Scope == RateLimiter.TenantScope);
        Assert.Equal(1, tenant.Used);   // the refused attempt did not count
    }

    [RedisFact]
    public async Task Concurrent_requests_cannot_race_past_the_limit()
    {
        var rules = new[] { Rule(RateLimiter.UserScope, 10) };

        var results = await Task.WhenAll(Enumerable.Range(0, 60).Select(_ => _limiter.CheckAsync(rules)));

        Assert.Equal(10, results.Count(r => r.Allowed));
    }

    [RedisFact]
    public async Task A_rule_with_no_limit_is_ignored_rather_than_blocking_everything()
    {
        var rules = new[] { Rule(RateLimiter.UserScope, 0) };

        var decision = await _limiter.CheckAsync(rules);

        Assert.True(decision.Allowed);
        Assert.Empty(decision.States);
    }

    [RedisFact]
    public async Task Token_rules_reserve_an_estimate_and_are_reconciled_to_the_real_count()
    {
        var rules = new[] { Rule(RateLimiter.ModelScope, limit: 1000, increment: 800) };

        var first = await _limiter.CheckAsync(rules);
        Assert.True(first.Allowed);

        // A second 800-token request will not fit against the reservation...
        Assert.False((await _limiter.CheckAsync(rules)).Allowed);

        // ...but the request only used 100, so the refund makes room again.
        await _limiter.ReconcileAsync(rules, estimate: 800, real: 100);
        Assert.True((await _limiter.CheckAsync(rules)).Allowed);
    }

    [RedisFact]
    public async Task Reconciling_upwards_charges_the_overrun()
    {
        var rules = new[] { Rule(RateLimiter.ModelScope, limit: 1000, increment: 100) };

        await _limiter.CheckAsync(rules);
        await _limiter.ReconcileAsync(rules, estimate: 100, real: 950);

        var decision = await _limiter.CheckAsync(rules);
        Assert.False(decision.Allowed);
    }

    [RedisFact]
    public async Task A_refund_can_never_drive_a_counter_negative()
    {
        var rules = new[] { Rule(RateLimiter.ModelScope, limit: 1000, increment: 10) };

        await _limiter.CheckAsync(rules);
        await _limiter.ReconcileAsync(rules, estimate: 10_000, real: 0);

        var decision = await _limiter.CheckAsync(rules);
        Assert.True(decision.Allowed);
        Assert.True(decision.States.Single().Used >= 0);
    }

    [RedisFact]
    public async Task Concurrency_slots_are_handed_out_up_to_the_limit_and_released()
    {
        var userId = Guid.NewGuid();

        var first = await _limiter.TryEnterAsync(userId, 2);
        var second = await _limiter.TryEnterAsync(userId, 2);
        var third = await _limiter.TryEnterAsync(userId, 2);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Null(third);

        await first!.ReleaseAsync();
        Assert.NotNull(await _limiter.TryEnterAsync(userId, 2));
    }

    [RedisFact]
    public async Task Releasing_a_slot_twice_does_not_hand_out_a_free_one()
    {
        var userId = Guid.NewGuid();
        var slot = await _limiter.TryEnterAsync(userId, 1);

        await slot!.ReleaseAsync();
        await slot.ReleaseAsync();

        Assert.NotNull(await _limiter.TryEnterAsync(userId, 1));
        Assert.Null(await _limiter.TryEnterAsync(userId, 1));
    }

    [RedisFact]
    public async Task No_concurrency_limit_means_no_slot_accounting()
    {
        var userId = Guid.NewGuid();

        for (var i = 0; i < 25; i++)
            Assert.NotNull(await _limiter.TryEnterAsync(userId, 0));
    }

    [RedisFact]
    public async Task Counter_keys_separate_every_level_of_the_hierarchy()
    {
        var org = Guid.NewGuid();
        var user = Guid.NewGuid();

        var keys = new[]
        {
            RateLimiter.PlatformKey("rpm", "m"),
            RateLimiter.TenantKey(org, "rpm", "m"),
            RateLimiter.ProviderKey(org, "anthropic", "rpm", "m"),
            RateLimiter.UserKey(user, "rpm", "m"),
            RateLimiter.ModelKey(user, "claude-sonnet-5", "rpm", "m"),
        };

        Assert.Equal(keys.Length, keys.Distinct().Count());
        Assert.Contains(org.ToString(), keys[1]);
        Assert.Contains("anthropic", keys[2]);
        await Task.CompletedTask;
    }
}
