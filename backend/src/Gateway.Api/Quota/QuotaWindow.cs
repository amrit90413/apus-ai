namespace Gateway.Api.Quota;

public enum WindowKind
{
    Rolling, // TTL starts when the window's first token is recorded (Anthropic-style)
    Fixed    // aligned to calendar boundaries (hour/day/week/month)
}

/// <summary>
/// A single fair-usage rule. The spec asks for hourly/daily/weekly/monthly plus
/// arbitrary minute-based windows (300, 720, 10080...). We express all of them as
/// a TokenLimit + a WindowMinutes duration, so "1M daily" and "reset every 720
/// minutes" are the same shape — no special-casing.
/// </summary>
public sealed record QuotaWindow(
    string Name,          // e.g. "w5h", "daily", "monthly" — used in the Redis key suffix
    long TokenLimit,      // e.g. 100_000, 1_000_000
    int WindowMinutes,    // 300, 1440, 10080, 43200...
    WindowKind Kind = WindowKind.Rolling)
{
    public int WindowSeconds => WindowMinutes * 60;

    public static QuotaWindow Every(int minutes, long tokenLimit, string name) =>
        new(name, tokenLimit, minutes, WindowKind.Rolling);

    // Convenience presets — all just sugar over the same record.
    public static QuotaWindow Hourly(long tokens)  => Every(60,    tokens, "hourly");
    public static QuotaWindow Daily(long tokens)   => Every(1440,  tokens, "daily");
    public static QuotaWindow Weekly(long tokens)  => Every(10080, tokens, "weekly");
    public static QuotaWindow Monthly(long tokens) => Every(43200, tokens, "monthly");
}

/// <summary>The principal a quota is charged against. IP is deliberately absent — per spec.</summary>
public sealed record QuotaPrincipal(Guid UserId, Guid WorkspaceId);

public sealed record WindowState(string Name, long Used, long Limit, int ResetInSeconds)
{
    public long Remaining => Math.Max(0, Limit - Used);
}

public sealed record QuotaDecision(
    bool Allowed,
    string? ViolatedWindow,
    IReadOnlyList<WindowState> Windows)
{
    /// <summary>The window the CLI should show a countdown for when blocked.</summary>
    public WindowState? Blocking =>
        ViolatedWindow is null ? null : Windows.FirstOrDefault(w => w.Name == ViolatedWindow);
}
