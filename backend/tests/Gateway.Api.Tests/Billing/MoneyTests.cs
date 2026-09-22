using Gateway.Api.Billing;

namespace Gateway.Api.Tests.Billing;

public sealed class MoneyTests
{
    [Theory]
    [InlineData("USD", 2)]
    [InlineData("INR", 2)]
    [InlineData("JPY", 0)]
    [InlineData("ZZZ", 2)]   // unknown currencies fall back to the common case
    public void Exponent_matches_the_currency(string code, int expected) =>
        Assert.Equal(expected, CurrencyInfo.Exponent(code));

    [Fact]
    public void FromMajor_converts_to_minor_units()
    {
        Assert.Equal(100_000, Money.FromMajor(1000m, "INR").Minor);
        Assert.Equal(1_234, Money.FromMajor(12.34m, "USD").Minor);
        Assert.Equal(500, Money.FromMajor(500m, "JPY").Minor);
    }

    [Fact]
    public void FromMajor_rounds_half_away_from_zero()
    {
        Assert.Equal(1_235, Money.FromMajor(12.345m, "USD").Minor);
        Assert.Equal(-1_235, Money.FromMajor(-12.345m, "USD").Minor);
    }

    [Fact]
    public void FromMajorCeiling_never_rounds_a_real_cost_down_to_nothing()
    {
        // A sub-paisa request must still cost something, or high-volume cheap calls
        // would be free.
        Assert.Equal(1, Money.FromMajorCeiling(0.0001m, "INR").Minor);
        Assert.Equal(1_235, Money.FromMajorCeiling(12.341m, "USD").Minor);
    }

    [Fact]
    public void ToMajor_round_trips()
    {
        var money = Money.FromMajor(1234.56m, "INR");
        Assert.Equal(1234.56m, money.ToMajor());
    }

    [Fact]
    public void Markup_is_applied_in_basis_points_and_rounds_up()
    {
        var cost = new Money(472, "INR");             // ₹4.72 provider cost

        Assert.Equal(472, cost.WithMarkupBps(0).Minor);
        Assert.Equal(567, cost.WithMarkupBps(2000).Minor);   // +20% => ₹5.67 (rounded up)
        Assert.Equal(944, cost.WithMarkupBps(10_000).Minor); // +100%
    }

    [Fact]
    public void Markup_never_reduces_a_cost()
    {
        var cost = new Money(1, "USD");
        Assert.True(cost.WithMarkupBps(1).Minor >= cost.Minor);
    }

    [Fact]
    public void Add_and_subtract_keep_the_currency()
    {
        var a = new Money(1000, "INR");
        var b = new Money(250, "INR");

        Assert.Equal(1250, a.Add(b).Minor);
        Assert.Equal(750, a.Subtract(b).Minor);
        Assert.Equal("INR", a.Add(b).Currency);
    }

    [Fact]
    public void Mixing_currencies_throws_rather_than_producing_a_wrong_number()
    {
        var rupees = new Money(1000, "INR");
        var dollars = new Money(1000, "USD");

        Assert.Throws<InvalidOperationException>(() => rupees.Add(dollars));
        Assert.Throws<InvalidOperationException>(() => rupees.Subtract(dollars));
    }

    [Fact]
    public void ToString_uses_the_currency_symbol_and_precision()
    {
        Assert.Equal("₹1,000.00", new Money(100_000, "INR").ToString());
        Assert.Equal("$4.72", new Money(472, "USD").ToString());
        Assert.Equal("¥500", new Money(500, "JPY").ToString());
    }

    [Fact]
    public void Normalize_defaults_to_usd_and_upcases()
    {
        Assert.Equal("USD", CurrencyInfo.Normalize(null));
        Assert.Equal("USD", CurrencyInfo.Normalize("  "));
        Assert.Equal("INR", CurrencyInfo.Normalize(" inr "));
    }

    [Fact]
    public void Billing_rate_falls_back_to_one_for_an_unconfigured_currency()
    {
        var options = new BillingOptions { UsdRates = { ["INR"] = 83.5m } };

        Assert.Equal(83.5m, options.RateFor("inr"));
        Assert.Equal(1m, options.RateFor("USD"));
        Assert.Equal(1m, options.RateFor("EUR"));
    }
}
