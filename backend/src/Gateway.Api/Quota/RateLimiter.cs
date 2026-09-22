using StackExchange.Redis;

namespace Gateway.Api.Quota;

/// <summary>One rule in the hierarchy: a counter key, its ceiling and its window.</summary>
public sealed record RateRule(string Scope, string Key, long Limit, int WindowSeconds, long Increment);

public sealed record RateState(string Scope, long Used, long Limit, int ResetInSeconds)
{
    public long Remaining => Limit <= 0 ? long.MaxValue : Math.Max(0, Limit - Used);
}

public sealed record RateDecision(bool Allowed, string? ViolatedScope, int? RetryAfterSeconds, IReadOnlyList<RateState> States)
{
    public static RateDecision Ok { get; } = new(true, null, null, Array.Empty<RateState>());
}

/// <summary>
/// Distributed rate limiting across platform, tenant, provider, user and model levels.
///
/// Redis holds the counters so every gateway replica sees the same numbers, and the
/// whole hierarchy is evaluated inside one Lua script: without that, two pods can each
/// read "under the limit" for the same user in the same millisecond.
/// </summary>
public sealed class RateLimiter
{
    public const string PlatformScope = "platform";
    public const string TenantScope = "tenant";
    public const string ProviderScope = "provider";
    public const string UserScope = "user";
    public const string ModelScope = "model";
    public const string ConcurrencyScope = "concurrency";

    /// <summary>Safety net so a crashed pod's in-flight slots drain instead of leaking forever.</summary>
    private static readonly TimeSpan ConcurrencyTtl = TimeSpan.FromMinutes(15);

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RateLimiter> _log;
    private string? _script;

    public RateLimiter(IConnectionMultiplexer redis, ILogger<RateLimiter> log)
    {
        _redis = redis; _log = log;
    }

    public async Task LoadScriptsAsync(string scriptDir)
    {
        _script = await File.ReadAllTextAsync(Path.Combine(scriptDir, "rate_limit.lua"));
        var prepared = LuaScript.Prepare(_script);
        foreach (var ep in _redis.GetEndPoints())
        {
            var server = _redis.GetServer(ep);
            if (!server.IsConnected || server.IsReplica) continue;
            await prepared.LoadAsync(server);
        }
    }

    /// <summary>
    /// Checks and records every rule atomically. Rules with a non-positive limit are
    /// tracked but never block, so an unset limit costs nothing.
    /// </summary>
    public async Task<RateDecision> CheckAsync(IReadOnlyList<RateRule> rules, CancellationToken ct = default)
    {
        if (_script is null) throw new InvalidOperationException("Scripts not loaded. Call LoadScriptsAsync at startup.");
        var applicable = rules.Where(r => r.Limit > 0).ToList();
        if (applicable.Count == 0) return RateDecision.Ok;

        var keys = applicable.Select(r => (RedisKey)r.Key).ToArray();
        var argv = new List<RedisValue> { applicable.Count };
        foreach (var rule in applicable)
        {
            argv.Add(rule.Limit);
            argv.Add(rule.WindowSeconds);
            argv.Add(rule.Increment);
        }

        var raw = (RedisResult[])(await _redis.GetDatabase().ScriptEvaluateAsync(_script, keys, argv.ToArray()))!;
        var allowed = (long)raw[0] == 1;
        var violatedIndex = (int)(long)raw[1];

        var states = new List<RateState>(applicable.Count);
        for (var i = 0; i < applicable.Count; i++)
        {
            var b = 2 + i * 3;
            states.Add(new RateState(applicable[i].Scope, (long)raw[b], (long)raw[b + 1], (int)(long)raw[b + 2]));
        }

        if (allowed) return new RateDecision(true, null, null, states);

        var violated = applicable[violatedIndex - 1];
        var state = states[violatedIndex - 1];
        _log.LogInformation("Rate limit {Scope} hit ({Used}/{Limit}); resets in {Reset}s",
            violated.Scope, state.Used, state.Limit, state.ResetInSeconds);
        return new RateDecision(false, violated.Scope, Math.Max(1, state.ResetInSeconds), states);
    }

    /// <summary>
    /// Refunds counters after the real token count is known, mirroring the quota
    /// engine's estimate-then-reconcile. Only token-based rules are adjusted.
    /// </summary>
    public async Task ReconcileAsync(IReadOnlyList<RateRule> tokenRules, long estimate, long real)
    {
        var delta = real - estimate;
        if (delta == 0 || tokenRules.Count == 0) return;

        var db = _redis.GetDatabase();
        foreach (var rule in tokenRules.Where(r => r.Limit > 0))
        {
            if (delta > 0) await db.StringIncrementAsync(rule.Key, delta);
            else await db.ScriptEvaluateAsync(
                // Clamp at zero and keep the window's TTL: a plain SET would drop it.
                "local v = tonumber(redis.call('GET', KEYS[1]) or '0') + tonumber(ARGV[1]) " +
                "if v < 0 then v = 0 end redis.call('SET', KEYS[1], v, 'KEEPTTL') return v",
                new RedisKey[] { rule.Key }, new RedisValue[] { delta });
        }
    }

    // ------------------------------------------------------------- concurrency

    /// <summary>
    /// Takes an in-flight slot for a user. Returns null when they are already at their
    /// concurrency ceiling. The slot must be released in a finally block.
    /// </summary>
    public async Task<ConcurrencySlot?> TryEnterAsync(Guid userId, int limit)
    {
        if (limit <= 0) return ConcurrencySlot.Unlimited;

        var key = $"rl:conc:user:{userId}";
        var db = _redis.GetDatabase();
        var value = await db.StringIncrementAsync(key);
        // Refresh on every entry: the TTL is a leak guard, not the limit itself.
        await db.KeyExpireAsync(key, ConcurrencyTtl);

        if (value <= limit) return new ConcurrencySlot(this, key);

        await db.StringDecrementAsync(key);
        return null;
    }

    internal async Task ReleaseConcurrencyAsync(string key)
    {
        var db = _redis.GetDatabase();
        // Clamp: a decrement racing a TTL expiry must not drive the counter negative,
        // which would silently hand out extra slots.
        await db.ScriptEvaluateAsync(
            "local v = tonumber(redis.call('GET', KEYS[1]) or '0') - 1 " +
            "if v <= 0 then redis.call('DEL', KEYS[1]) return 0 end redis.call('SET', KEYS[1], v, 'KEEPTTL') return v",
            new RedisKey[] { key });
    }

    // ----------------------------------------------------------------- key help

    public static string PlatformKey(string unit, string window) => $"rl:plat:{unit}:{window}";
    public static string TenantKey(Guid org, string unit, string window) => $"rl:org:{org}:{unit}:{window}";
    public static string ProviderKey(Guid org, string provider, string unit, string window) => $"rl:org:{org}:prov:{provider}:{unit}:{window}";
    public static string UserKey(Guid user, string unit, string window) => $"rl:user:{user}:{unit}:{window}";
    public static string ModelKey(Guid user, string model, string unit, string window) => $"rl:user:{user}:model:{model}:{unit}:{window}";
}

/// <summary>A held concurrency slot. Release exactly once, in a finally.</summary>
public sealed class ConcurrencySlot
{
    private readonly RateLimiter? _limiter;
    private readonly string? _key;
    private int _released;

    internal ConcurrencySlot(RateLimiter limiter, string key) { _limiter = limiter; _key = key; }
    private ConcurrencySlot() { }

    /// <summary>A slot for a caller with no concurrency limit; releasing is a no-op.</summary>
    public static ConcurrencySlot Unlimited { get; } = new();

    public async Task ReleaseAsync()
    {
        if (_limiter is null || _key is null) return;
        if (Interlocked.Exchange(ref _released, 1) == 1) return;
        try { await _limiter.ReleaseConcurrencyAsync(_key); }
        catch { /* the TTL reclaims the slot; never fail a response over this */ }
    }
}
