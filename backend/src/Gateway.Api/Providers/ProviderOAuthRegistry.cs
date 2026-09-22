namespace Gateway.Api.Providers;

/// <summary>
/// Per-provider OAuth client configuration.
///
/// Read from `Providers:OAuth:&lt;provider&gt;:*`, with the legacy `Anthropic:OAuth:*`
/// section honoured for Anthropic so existing deployments keep working. A provider
/// with no configured client simply has no OAuth option in the connect UI — the
/// architecture is ready for one the day the provider issues APUS a client, with no
/// code change beyond configuration.
///
/// Tenants can never supply their own authorize/token URLs: allowing that would let
/// one tenant point the gateway's OAuth flow at an endpoint they control.
/// </summary>
public sealed class ProviderOAuthRegistry
{
    private readonly Dictionary<string, ProviderOAuthOptions> _byProvider;
    private readonly ILogger<ProviderOAuthRegistry> _log;

    public ProviderOAuthRegistry(IConfiguration config, AnthropicOAuthOptions legacyAnthropic, ILogger<ProviderOAuthRegistry> log)
    {
        _log = log;
        _byProvider = new Dictionary<string, ProviderOAuthOptions>(StringComparer.OrdinalIgnoreCase);

        foreach (var descriptor in ProviderCatalog.Descriptors)
        {
            if (!descriptor.Supports(Domain.ConnectionType.OAuth)) continue;

            var section = config.GetSection($"Providers:OAuth:{descriptor.Id}");
            var opt = section.Exists() ? section.Get<ProviderOAuthOptions>() : null;

            // Legacy alias wins only when the general section is absent or unusable.
            if ((opt is null || !opt.Enabled) && descriptor.Id == ProviderCatalog.Anthropic && legacyAnthropic.Enabled)
                opt = legacyAnthropic;

            if (opt is null) continue;
            _byProvider[descriptor.Id] = opt;

            if (opt.Enabled)
                _log.LogInformation("Provider OAuth enabled for {Provider} (client {ClientId}).", descriptor.Id, opt.ClientId);
        }
    }

    /// <summary>Configured options for a provider, or null when OAuth is not set up for it.</summary>
    public ProviderOAuthOptions? For(string provider) =>
        _byProvider.TryGetValue(provider, out var o) && o.Enabled ? o : null;

    public bool IsEnabled(string provider) => For(provider) is not null;

    public IReadOnlyCollection<string> EnabledProviders =>
        _byProvider.Where(kv => kv.Value.Enabled).Select(kv => kv.Key).ToList();

    /// <summary>
    /// The redirect URI the provider will send the admin back to. Compared exactly
    /// against what the provider returns, and never taken from the request.
    /// </summary>
    public string? RedirectUriFor(string provider) => For(provider)?.RedirectUri;
}
