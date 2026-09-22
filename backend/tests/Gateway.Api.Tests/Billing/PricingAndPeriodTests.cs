using Gateway.Api.Allowances;
using Gateway.Api.Billing;
using Gateway.Api.Domain;
using Gateway.Api.Persistence;
using Gateway.Api.Providers;
using Gateway.Api.Tests.Infrastructure;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gateway.Api.Tests.Billing;

public sealed class AllowanceCalendarTests
{
    [Fact]
    public void A_period_is_a_utc_calendar_month()
    {
        var (start, end) = AllowanceCalendar.Current(new DateTimeOffset(2026, 3, 17, 14, 22, 0, TimeSpan.Zero));

        Assert.Equal(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), start);
        Assert.Equal(new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero), end);
    }

    [Fact]
    public void Periods_abut_exactly_so_no_request_falls_between_them()
    {
        var at = new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.Zero);

        var (_, currentEnd) = AllowanceCalendar.Current(at);
        var (nextStart, _) = AllowanceCalendar.Next(at);

        Assert.Equal(currentEnd, nextStart);
    }

    [Fact]
    public void The_year_boundary_rolls_over_correctly()
    {
        var (start, end) = AllowanceCalendar.Next(new DateTimeOffset(2026, 12, 5, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), start);
        Assert.Equal(new DateTimeOffset(2027, 2, 1, 0, 0, 0, TimeSpan.Zero), end);
    }

    [Fact]
    public void A_local_offset_is_normalised_to_utc()
    {
        // 00:30 on the 1st in IST is still the previous month in UTC.
        var ist = new DateTimeOffset(2026, 4, 1, 0, 30, 0, TimeSpan.FromHours(5.5));

        var (start, _) = AllowanceCalendar.Current(ist);

        Assert.Equal(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), start);
    }

    [Fact]
    public void Day_boundaries_are_utc_midnights()
    {
        var (start, end) = AllowanceCalendar.Day(new DateTimeOffset(2026, 5, 9, 18, 0, 0, TimeSpan.Zero));

        Assert.Equal(new DateTimeOffset(2026, 5, 9, 0, 0, 0, TimeSpan.Zero), start);
        Assert.Equal(new DateTimeOffset(2026, 5, 10, 0, 0, 0, TimeSpan.Zero), end);
    }
}

/// <summary>
/// Cost calculation against a real price list. The numbers here are the ones that end
/// up on an invoice, so they are pinned rather than approximated.
/// </summary>
public sealed class PricingServiceTests : IAsyncLifetime
{
    private readonly PostgresDatabase _db = new();
    private PricingService _pricing = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        if (!TestInfra.HasPostgres) return;

        _pricing = new PricingService(_db.ScopeFactory(), new MemoryCache(new MemoryCacheOptions()), NullLogger<PricingService>.Instance);
        await PricingSeed.RunAsync(_db.ScopeFactory().CreateScope().ServiceProvider, NullLogger.Instance);
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private static readonly OrganizationBilling Usd = new("USD", 1m, 0);
    private static readonly OrganizationBilling Inr = new("INR", 83m, 0);

    [PostgresFact]
    public async Task The_seed_covers_every_provider_that_can_serve_a_model()
    {
        foreach (var provider in new[] { ProviderCatalog.Anthropic, ProviderCatalog.Bedrock, ProviderCatalog.Vertex })
            Assert.NotNull(await _pricing.ResolveAsync(provider, "claude-sonnet-5", DateTimeOffset.UtcNow, default));

        Assert.NotNull(await _pricing.ResolveAsync(ProviderCatalog.OpenAi, "gpt-4o", DateTimeOffset.UtcNow, default));
        Assert.NotNull(await _pricing.ResolveAsync(ProviderCatalog.Gemini, "gemini-2.5-pro", DateTimeOffset.UtcNow, default));
    }

    [PostgresFact]
    public async Task Cost_is_computed_from_the_price_list_in_force()
    {
        // claude-sonnet-5: $2/Mtok in, $10/Mtok out.
        // 1M in + 1M out = $12.00 => 1200 cents.
        var cost = await _pricing.CalculateAsync(ProviderCatalog.Anthropic, "claude-sonnet-5",
            new BilledTokens(1_000_000, 1_000_000, 0, 0), Usd, DateTimeOffset.UtcNow, default);

        Assert.Equal(1_200, cost.ProviderCost.Minor);
        Assert.Equal("USD", cost.ProviderCost.Currency);
        Assert.NotNull(cost.PricingId);
    }

    [PostgresFact]
    public async Task Cached_reads_are_billed_at_the_cache_rate_not_the_input_rate()
    {
        var fresh = await _pricing.CalculateAsync(ProviderCatalog.Anthropic, "claude-sonnet-5",
            new BilledTokens(1_000_000, 0, 0, 0), Usd, DateTimeOffset.UtcNow, default);
        var cached = await _pricing.CalculateAsync(ProviderCatalog.Anthropic, "claude-sonnet-5",
            new BilledTokens(0, 0, 1_000_000, 0), Usd, DateTimeOffset.UtcNow, default);

        Assert.Equal(200, fresh.ProviderCost.Minor);     // $2.00
        Assert.Equal(20, cached.ProviderCost.Minor);     // $0.20
    }

    [PostgresFact]
    public async Task The_tenant_is_billed_in_its_own_currency_at_the_configured_rate()
    {
        // $12.00 at 83 INR/USD = ₹996.00 = 99,600 paise.
        var cost = await _pricing.CalculateAsync(ProviderCatalog.Anthropic, "claude-sonnet-5",
            new BilledTokens(1_000_000, 1_000_000, 0, 0), Inr, DateTimeOffset.UtcNow, default);

        Assert.Equal("INR", cost.ProviderCost.Currency);
        Assert.Equal(99_600, cost.ProviderCost.Minor);
    }

    [PostgresFact]
    public async Task Customer_cost_carries_the_markup_while_provider_cost_stays_true()
    {
        var billing = new OrganizationBilling("INR", 83m, MarkupBps: 2500);   // +25%

        var cost = await _pricing.CalculateAsync(ProviderCatalog.Anthropic, "claude-sonnet-5",
            new BilledTokens(1_000_000, 1_000_000, 0, 0), billing, DateTimeOffset.UtcNow, default);

        Assert.Equal(99_600, cost.ProviderCost.Minor);
        Assert.Equal(124_500, cost.CustomerCost.Minor);
        Assert.True(cost.CustomerCost.Minor > cost.ProviderCost.Minor, "margin must be positive");
    }

    [PostgresFact]
    public async Task An_unpriced_model_costs_nothing_rather_than_failing_the_request()
    {
        var cost = await _pricing.CalculateAsync(ProviderCatalog.Anthropic, "claude-not-released-yet",
            new BilledTokens(1000, 1000, 0, 0), Usd, DateTimeOffset.UtcNow, default);

        Assert.Equal(0, cost.ProviderCost.Minor);
        Assert.Null(cost.PricingId);
    }

    [PostgresFact]
    public async Task A_later_price_version_does_not_change_an_earlier_cost()
    {
        // Historical cost must stay reproducible after a price change.
        var changeover = DateTimeOffset.UtcNow.AddDays(-1);
        await using (var db = _db.NewContext())
        {
            db.Pricing.Add(new ProviderModelPricing
            {
                Provider = ProviderCatalog.Anthropic, Model = "claude-priced-twice",
                InputPerMTok = 1m, OutputPerMTok = 1m,
                EffectiveFrom = DateTimeOffset.UtcNow.AddDays(-30), EffectiveTo = changeover,
            });
            db.Pricing.Add(new ProviderModelPricing
            {
                Provider = ProviderCatalog.Anthropic, Model = "claude-priced-twice",
                InputPerMTok = 4m, OutputPerMTok = 4m,
                EffectiveFrom = changeover,
            });
            await db.SaveChangesAsync();
        }

        var tokens = new BilledTokens(1_000_000, 0, 0, 0);
        var then = await _pricing.CalculateAsync(ProviderCatalog.Anthropic, "claude-priced-twice", tokens, Usd, changeover.AddDays(-5), default);
        _pricing.Invalidate(ProviderCatalog.Anthropic, "claude-priced-twice");
        var now = await _pricing.CalculateAsync(ProviderCatalog.Anthropic, "claude-priced-twice", tokens, Usd, DateTimeOffset.UtcNow, default);

        Assert.Equal(100, then.ProviderCost.Minor);
        Assert.Equal(400, now.ProviderCost.Minor);
        Assert.NotEqual(then.PricingId, now.PricingId);
    }

    [PostgresFact]
    public async Task Seeding_twice_does_not_duplicate_the_price_list()
    {
        await PricingSeed.RunAsync(_db.ScopeFactory().CreateScope().ServiceProvider, NullLogger.Instance);

        await using var db = _db.NewContext();
        var duplicates = db.Pricing.GroupBy(p => new { p.Provider, p.Model, p.EffectiveFrom }).Count(g => g.Count() > 1);

        Assert.Equal(0, duplicates);
    }

    [PostgresFact]
    public async Task The_estimate_is_the_customer_price_because_that_is_what_is_reserved()
    {
        var billing = new OrganizationBilling("USD", 1m, MarkupBps: 5000);

        var estimate = await _pricing.EstimateAsync(ProviderCatalog.Anthropic, "claude-sonnet-5",
            new BilledTokens(0, 1_000_000, 0, 0), billing, DateTimeOffset.UtcNow, default);

        Assert.Equal(1_500, estimate.Minor);   // $10.00 + 50%
    }
}
