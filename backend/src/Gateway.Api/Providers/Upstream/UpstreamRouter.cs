namespace Gateway.Api.Providers.Upstream;

/// <summary>
/// Maps a provider id to the adapter that speaks its wire protocol.
///
/// This is the whole extension point for a new vendor: register an
/// <see cref="IUpstreamAdapter"/> and add a catalog descriptor. Quotas, allowances,
/// the ledger, RBAC and the dashboards are untouched.
/// </summary>
public sealed class UpstreamRouter
{
    private readonly Dictionary<string, IUpstreamAdapter> _adapters;
    private readonly ILogger<UpstreamRouter> _log;

    public UpstreamRouter(IEnumerable<IUpstreamAdapter> adapters, ILogger<UpstreamRouter> log)
    {
        _adapters = adapters.ToDictionary(a => a.Provider, StringComparer.OrdinalIgnoreCase);
        _log = log;
    }

    public bool Supports(string provider) => _adapters.ContainsKey(provider);

    public IUpstreamAdapter For(string provider) =>
        _adapters.TryGetValue(provider, out var adapter)
            ? adapter
            : throw new NotSupportedException($"No upstream adapter is registered for provider '{provider}'.");

    public IReadOnlyCollection<string> Providers => _adapters.Keys;
}
