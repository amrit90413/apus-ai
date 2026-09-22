using System.Diagnostics.Metrics;
using Gateway.Api.Billing;
using Gateway.Api.Domain;

namespace Gateway.Api.Gateway;

/// <summary>
/// Gateway telemetry. Dimensions are deliberately low-cardinality — provider, model,
/// outcome — so the series count stays bounded; anything per-user or per-tenant belongs
/// in the usage ledger, not in metrics.
/// </summary>
public sealed class GatewayMetrics
{
    public const string MeterName = "apus.gateway";

    private readonly Counter<long> _admitted;
    private readonly Counter<long> _denied;
    private readonly Counter<long> _completed;
    private readonly Counter<long> _tokens;
    private readonly Counter<long> _costMinor;
    private readonly Counter<long> _providerFailures;
    private readonly Counter<long> _fallbacks;
    private readonly Counter<long> _oauthFailures;
    private readonly Counter<long> _refreshFailures;
    private readonly Histogram<double> _latency;

    public GatewayMetrics(IMeterFactory factory)
    {
        var meter = factory.Create(MeterName);
        _admitted = meter.CreateCounter<long>("apus.gateway.requests.admitted", "requests", "Requests that passed every policy check.");
        _denied = meter.CreateCounter<long>("apus.gateway.requests.denied", "requests", "Requests refused by the policy pipeline.");
        _completed = meter.CreateCounter<long>("apus.gateway.requests.completed", "requests", "Requests settled after the provider responded.");
        _tokens = meter.CreateCounter<long>("apus.gateway.tokens", "tokens", "Billed tokens by provider, model and direction.");
        _costMinor = meter.CreateCounter<long>("apus.gateway.cost.minor", "minor", "Cost in currency minor units by side (provider/customer).");
        _providerFailures = meter.CreateCounter<long>("apus.gateway.provider.failures", "failures", "Upstream provider failures.");
        _fallbacks = meter.CreateCounter<long>("apus.gateway.provider.fallbacks", "events", "Requests served by a fallback provider.");
        _oauthFailures = meter.CreateCounter<long>("apus.gateway.oauth.failures", "failures", "OAuth authorization or exchange failures.");
        _refreshFailures = meter.CreateCounter<long>("apus.gateway.credential.refresh_failures", "failures", "Credential refresh failures.");
        _latency = meter.CreateHistogram<double>("apus.gateway.latency", "ms", "End-to-end gateway latency.");
    }

    public void RecordAdmitted(string provider, string model) =>
        _admitted.Add(1, Tag("provider", provider), Tag("model", model));

    public void RecordDenied(string errorCode, string provider) =>
        _denied.Add(1, Tag("code", errorCode), Tag("provider", provider));

    public void RecordCompleted(string provider, string model, GatewayOutcome outcome, CostBreakdown cost, TimeSpan elapsed)
    {
        var status = outcome.Status.ToString().ToLowerInvariant();
        _completed.Add(1, Tag("provider", provider), Tag("model", model), Tag("status", status));
        _latency.Record(elapsed.TotalMilliseconds, Tag("provider", provider), Tag("model", model));

        if (outcome.Tokens.Input > 0) _tokens.Add(outcome.Tokens.Input, Tag("provider", provider), Tag("model", model), Tag("kind", "input"));
        if (outcome.Tokens.Output > 0) _tokens.Add(outcome.Tokens.Output, Tag("provider", provider), Tag("model", model), Tag("kind", "output"));
        if (outcome.Tokens.CachedInput > 0) _tokens.Add(outcome.Tokens.CachedInput, Tag("provider", provider), Tag("model", model), Tag("kind", "cache_read"));
        if (outcome.Tokens.CacheWrite > 0) _tokens.Add(outcome.Tokens.CacheWrite, Tag("provider", provider), Tag("model", model), Tag("kind", "cache_write"));

        if (cost.ProviderCost.Minor > 0)
            _costMinor.Add(cost.ProviderCost.Minor, Tag("provider", provider), Tag("side", "provider"), Tag("currency", cost.ProviderCost.Currency));
        if (cost.CustomerCost.Minor > 0)
            _costMinor.Add(cost.CustomerCost.Minor, Tag("provider", provider), Tag("side", "customer"), Tag("currency", cost.CustomerCost.Currency));

        if (outcome.Status is UsageStatus.Failed)
            _providerFailures.Add(1, Tag("provider", provider), Tag("category", outcome.FailureCategory ?? "unknown"));
    }

    public void RecordFallback(string from, string to) =>
        _fallbacks.Add(1, Tag("from", from), Tag("to", to));

    public void RecordOAuthFailure(string provider, string reason) =>
        _oauthFailures.Add(1, Tag("provider", provider), Tag("reason", reason));

    public void RecordRefreshFailure(string provider, string reason) =>
        _refreshFailures.Add(1, Tag("provider", provider), Tag("reason", reason));

    private static KeyValuePair<string, object?> Tag(string key, string value) => new(key, value);
}
