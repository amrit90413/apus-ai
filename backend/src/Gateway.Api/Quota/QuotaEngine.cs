using StackExchange.Redis;

namespace Gateway.Api.Quota;

/// <summary>
/// The Redis-backed fair-usage engine. Two operations:
///   1. ReserveAsync  — atomic check + estimate reservation BEFORE the AI call.
///   2. ReconcileAsync — adjust counters to the real token count AFTER the call.
///
/// Quota ownership is (UserId, WorkspaceId) — never IP (see spec). Each principal
/// is checked against BOTH its per-user windows and its workspace windows in the
/// same atomic script call, so a user can't exceed either.
/// </summary>
public sealed class QuotaEngine
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<QuotaEngine> _log;
    private LuaScript? _check;
    private LuaScript? _reconcile;

    public QuotaEngine(IConnectionMultiplexer redis, ILogger<QuotaEngine> log)
    {
        _redis = redis;
        _log = log;
    }

    public async Task LoadScriptsAsync(string scriptDir)
    {
        _check = LuaScript.Prepare(await File.ReadAllTextAsync(Path.Combine(scriptDir, "quota_check.lua")));
        _reconcile = LuaScript.Prepare(await File.ReadAllTextAsync(Path.Combine(scriptDir, "quota_reconcile.lua")));
        // Pre-load into every server so the first request isn't slow / NOSCRIPT-prone.
        foreach (var ep in _redis.GetEndPoints())
        {
            var server = _redis.GetServer(ep);
            if (!server.IsConnected || server.IsReplica) continue;
            await _check.LoadAsync(server);
            await _reconcile.LoadAsync(server);
        }
    }

    private static string Key(string scope, Guid id, string window) => $"quota:{scope}:{id}:{window}";

    private (RedisKey[] keys, RedisValue[] argv) Build(
        QuotaPrincipal p,
        IReadOnlyList<QuotaWindow> userWindows,
        IReadOnlyList<QuotaWindow> workspaceWindows,
        long estimate,
        out List<(string name, QuotaWindow w)> order)
    {
        order = new();
        var keys = new List<RedisKey>();
        var argv = new List<RedisValue> { estimate };

        void Add(string scope, Guid id, IReadOnlyList<QuotaWindow> windows)
        {
            foreach (var w in windows)
            {
                keys.Add(Key(scope, id, $"{scope}:{w.Name}"));
                argv.Add(w.TokenLimit);
                argv.Add(w.WindowSeconds);
                order.Add(($"{scope}:{w.Name}", w));
            }
        }

        Add("user", p.UserId, userWindows);
        Add("workspace", p.WorkspaceId, workspaceWindows);
        return (keys.ToArray(), argv.ToArray());
    }

    public async Task<QuotaDecision> ReserveAsync(
        QuotaPrincipal p,
        IReadOnlyList<QuotaWindow> userWindows,
        IReadOnlyList<QuotaWindow> workspaceWindows,
        long estimatedTokens,
        CancellationToken ct = default)
    {
        if (_check is null) throw new InvalidOperationException("Scripts not loaded. Call LoadScriptsAsync at startup.");

        var (keys, argv) = Build(p, userWindows, workspaceWindows, estimatedTokens, out var order);
        var db = _redis.GetDatabase();

        var raw = (RedisResult[])(await db.ScriptEvaluateAsync(_check.OriginalScript, keys, argv))!;
        var allowed = (long)raw[0] == 1;
        var violatedIndex = (int)(long)raw[1];

        var windows = new List<WindowState>(order.Count);
        for (int i = 0; i < order.Count; i++)
        {
            int baseIdx = 2 + i * 3;
            windows.Add(new WindowState(
                order[i].name,
                (long)raw[baseIdx],
                (long)raw[baseIdx + 1],
                (int)(long)raw[baseIdx + 2]));
        }

        var violated = violatedIndex == 0 ? null : order[violatedIndex - 1].name;
        if (!allowed)
            _log.LogInformation("Quota blocked user {User} workspace {Ws} on {Window}",
                p.UserId, p.WorkspaceId, violated);

        return new QuotaDecision(allowed, violated, windows);
    }

    /// <summary>
    /// Adjust every reserved window to the real token total. Call once the provider
    /// response (or stream) completes. delta = realTokens - estimatedTokens.
    /// </summary>
    public async Task ReconcileAsync(
        QuotaPrincipal p,
        IReadOnlyList<QuotaWindow> userWindows,
        IReadOnlyList<QuotaWindow> workspaceWindows,
        long estimatedTokens,
        long realTokens,
        CancellationToken ct = default)
    {
        if (_reconcile is null) return;
        var delta = realTokens - estimatedTokens;
        if (delta == 0) return;

        var (keys, _) = Build(p, userWindows, workspaceWindows, 0, out _);
        var db = _redis.GetDatabase();
        await db.ScriptEvaluateAsync(_reconcile.OriginalScript, keys, new RedisValue[] { delta });
    }
}
