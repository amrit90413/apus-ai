namespace Gateway.Api.Security;

/// <summary>
/// Rollout switches for the provider-connection and allowance features.
///
/// Each flag is off, on, or on for a named list of organizations, which is the
/// internal-tenants → beta → GA path: set `Features:AiProviderOauth:Organizations`
/// to the pilot tenants first, then flip `Enabled` once it holds.
/// </summary>
public sealed class FeatureFlagOptions
{
    public Dictionary<string, FeatureFlagSetting> Flags { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class FeatureFlagSetting
{
    /// <summary>On for everyone. When false, only the organizations listed below get it.</summary>
    public bool Enabled { get; set; }
    /// <summary>Organization ids (or slugs) the flag is on for while Enabled is false.</summary>
    public List<string> Organizations { get; set; } = new();
}

public static class FeatureFlagNames
{
    public const string ProviderConnections = "AI_PROVIDER_CONNECTIONS";
    public const string ProviderOAuth = "AI_PROVIDER_OAUTH";
    public const string ChildAllowances = "AI_CHILD_ALLOWANCES";
    public const string ProviderFallback = "AI_PROVIDER_FALLBACK";
    public const string UsageBilling = "AI_USAGE_BILLING";
}

public interface IFeatureFlags
{
    bool IsEnabled(string flag, Guid? organizationId = null);
    IReadOnlyDictionary<string, bool> Snapshot(Guid? organizationId = null);
}

public sealed class FeatureFlags : IFeatureFlags
{
    // Defaults for a flag nobody configured. The connection and allowance features are
    // on by default because the gateway cannot serve traffic without them; the two
    // genuinely optional behaviours default off.
    private static readonly Dictionary<string, bool> Defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        [FeatureFlagNames.ProviderConnections] = true,
        [FeatureFlagNames.ProviderOAuth] = true,
        [FeatureFlagNames.ChildAllowances] = true,
        [FeatureFlagNames.ProviderFallback] = false,
        [FeatureFlagNames.UsageBilling] = true,
    };

    private readonly FeatureFlagOptions _opt;

    public FeatureFlags(FeatureFlagOptions opt) => _opt = opt;

    public bool IsEnabled(string flag, Guid? organizationId = null)
    {
        if (!_opt.Flags.TryGetValue(flag, out var setting))
            return Defaults.TryGetValue(flag, out var d) && d;

        if (setting.Enabled) return true;
        if (organizationId is null || setting.Organizations.Count == 0) return false;

        var id = organizationId.Value.ToString();
        return setting.Organizations.Any(o => string.Equals(o, id, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyDictionary<string, bool> Snapshot(Guid? organizationId = null) =>
        Defaults.Keys.ToDictionary(f => f, f => IsEnabled(f, organizationId), StringComparer.OrdinalIgnoreCase);
}
