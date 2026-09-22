using Gateway.Api.Domain;
using Gateway.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Gateway.Api.Billing;

/// <summary>Token counts a cost is computed from. Cached reads and writes are billed tokens too.</summary>
public readonly record struct BilledTokens(int Input, int Output, int CachedInput, int CacheWrite)
{
    public long Total => (long)Input + Output + CachedInput + CacheWrite;
    public static readonly BilledTokens None = new(0, 0, 0, 0);
}

/// <summary>
/// What a request cost. ProviderCost is what the upstream bills the tenant's account;
/// CustomerCost is what APUS charges for it. They are deliberately separate figures —
/// overwriting one with the other destroys margin and reconciliation reporting.
/// </summary>
public readonly record struct CostBreakdown(Money ProviderCost, Money CustomerCost, Guid? PricingId)
{
    public static CostBreakdown Zero(string currency) =>
        new(Money.Zero(currency), Money.Zero(currency), null);
}

public interface IPricingService
{
    /// <summary>The price list version in force for a model at an instant, or null when unpriced.</summary>
    Task<ProviderModelPricing?> ResolveAsync(string provider, string model, DateTimeOffset at, CancellationToken ct);

    /// <summary>
    /// Cost of a completed request in the tenant's currency. An unpriced model yields
    /// zero cost with a null pricing id — the request is still recorded, and the
    /// gateway logs it so pricing can be added.
    /// </summary>
    Task<CostBreakdown> CalculateAsync(
        string provider, string model, BilledTokens tokens, OrganizationBilling billing, DateTimeOffset at, CancellationToken ct);

    /// <summary>Worst-case cost of a request about to be made, used to size the allowance reservation.</summary>
    Task<Money> EstimateAsync(
        string provider, string model, BilledTokens upperBound, OrganizationBilling billing, DateTimeOffset at, CancellationToken ct);

    void Invalidate(string provider, string model);
}

/// <summary>The billing settings of one organization, resolved once per request.</summary>
public readonly record struct OrganizationBilling(string Currency, decimal UsdRate, int MarkupBps)
{
    public static OrganizationBilling ForUsd() => new("USD", 1m, 0);
}

public sealed class PricingService : IPricingService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<PricingService> _log;

    public PricingService(IServiceScopeFactory scopeFactory, IMemoryCache cache, ILogger<PricingService> log)
    {
        _scopeFactory = scopeFactory; _cache = cache; _log = log;
    }

    private static string CacheKey(string provider, string model) => $"price:{provider}:{model}".ToLowerInvariant();

    public async Task<ProviderModelPricing?> ResolveAsync(string provider, string model, DateTimeOffset at, CancellationToken ct)
    {
        var key = CacheKey(provider, model);
        if (_cache.TryGetValue(key, out ProviderModelPricing? cached)) return cached;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

        // Newest version whose window contains `at`. Historical costs stay reproducible
        // because a ledger row stores the id of the version it used.
        var row = await db.Pricing.AsNoTracking()
            .Where(p => p.Provider == provider && p.Model == model
                        && p.EffectiveFrom <= at && (p.EffectiveTo == null || p.EffectiveTo > at))
            .OrderByDescending(p => p.EffectiveFrom)
            .FirstOrDefaultAsync(ct);

        // Cache misses too, so an unpriced model does not hit Postgres on every request.
        _cache.Set(key, row, CacheTtl);
        return row;
    }

    public async Task<CostBreakdown> CalculateAsync(
        string provider, string model, BilledTokens tokens, OrganizationBilling billing, DateTimeOffset at, CancellationToken ct)
    {
        var price = await ResolveAsync(provider, model, at, ct);
        if (price is null)
        {
            if (tokens.Total > 0)
                _log.LogWarning("No pricing for {Provider}/{Model}; the request is recorded at zero cost.", provider, model);
            return CostBreakdown.Zero(billing.Currency);
        }

        var usd =
            tokens.Input / 1_000_000m * price.InputPerMTok +
            tokens.Output / 1_000_000m * price.OutputPerMTok +
            tokens.CachedInput / 1_000_000m * price.CachedInputPerMTok +
            tokens.CacheWrite / 1_000_000m * price.CacheWritePerMTok;

        // Published prices are USD; the tenant is billed in its own currency at the
        // configured rate so a report does not move because an FX feed did.
        var providerCost = Money.FromMajorCeiling(usd * billing.UsdRate, billing.Currency);
        var customerCost = providerCost.WithMarkupBps(billing.MarkupBps);
        return new CostBreakdown(providerCost, customerCost, price.Id);
    }

    public async Task<Money> EstimateAsync(
        string provider, string model, BilledTokens upperBound, OrganizationBilling billing, DateTimeOffset at, CancellationToken ct)
    {
        var breakdown = await CalculateAsync(provider, model, upperBound, billing, at, ct);
        return breakdown.CustomerCost;
    }

    public void Invalidate(string provider, string model) => _cache.Remove(CacheKey(provider, model));
}
