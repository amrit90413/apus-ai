using System.Globalization;

namespace Gateway.Api.Billing;

/// <summary>
/// Currency metadata. Exponent is the number of decimal places the currency's minor
/// unit has (2 for paise/cents, 0 for yen), per ISO 4217.
/// </summary>
public static class CurrencyInfo
{
    private static readonly Dictionary<string, int> Exponents = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USD"] = 2, ["INR"] = 2, ["EUR"] = 2, ["GBP"] = 2, ["AUD"] = 2, ["CAD"] = 2,
        ["SGD"] = 2, ["AED"] = 2, ["CHF"] = 2, ["JPY"] = 0, ["KRW"] = 0,
    };

    private static readonly Dictionary<string, string> Symbols = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USD"] = "$", ["INR"] = "₹", ["EUR"] = "€", ["GBP"] = "£", ["JPY"] = "¥",
    };

    public static bool IsKnown(string code) => Exponents.ContainsKey(code);

    public static int Exponent(string code) => Exponents.TryGetValue(code, out var e) ? e : 2;

    public static string Symbol(string code) => Symbols.TryGetValue(code, out var s) ? s : code.ToUpperInvariant() + " ";

    public static string Normalize(string? code) =>
        string.IsNullOrWhiteSpace(code) ? "USD" : code.Trim().ToUpperInvariant();
}

/// <summary>
/// A monetary amount as an integer count of minor units (paise, cents) plus its
/// currency. Financial amounts never touch double/float: every allowance, cost and
/// ledger figure in the gateway is a Money or a raw minor-unit long.
///
/// Arithmetic is only defined between amounts of the same currency; mixing throws
/// rather than silently producing a wrong number.
/// </summary>
public readonly record struct Money(long Minor, string Currency)
{
    public static Money Zero(string currency) => new(0, CurrencyInfo.Normalize(currency));

    /// <summary>Rounds half away from zero, the convention users expect on an invoice.</summary>
    public static Money FromMajor(decimal major, string currency)
    {
        var code = CurrencyInfo.Normalize(currency);
        var scale = Pow10(CurrencyInfo.Exponent(code));
        return new Money(decimal.ToInt64(Math.Round(major * scale, 0, MidpointRounding.AwayFromZero)), code);
    }

    /// <summary>Rounds up, so a fractional cost is never billed as zero and margin is never negative.</summary>
    public static Money FromMajorCeiling(decimal major, string currency)
    {
        var code = CurrencyInfo.Normalize(currency);
        var scale = Pow10(CurrencyInfo.Exponent(code));
        return new Money(decimal.ToInt64(Math.Ceiling(major * scale)), code);
    }

    public decimal ToMajor() => Minor / (decimal)Pow10(CurrencyInfo.Exponent(Currency));

    public Money Add(Money other) { Require(other); return this with { Minor = Minor + other.Minor }; }
    public Money Subtract(Money other) { Require(other); return this with { Minor = Minor - other.Minor }; }

    /// <summary>Applies a basis-point markup (10000 bps = +100%), rounding up.</summary>
    public Money WithMarkupBps(int bps)
    {
        if (bps <= 0) return this;
        var scaled = (decimal)Minor * (10_000m + bps) / 10_000m;
        return this with { Minor = decimal.ToInt64(Math.Ceiling(scaled)) };
    }

    public override string ToString() =>
        CurrencyInfo.Symbol(Currency) + ToMajor().ToString(
            "N" + CurrencyInfo.Exponent(Currency).ToString(CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture);

    private void Require(Money other)
    {
        if (!string.Equals(Currency, other.Currency, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Cannot combine {Currency} with {other.Currency}.");
    }

    private static long Pow10(int n)
    {
        long v = 1;
        for (var i = 0; i < n; i++) v *= 10;
        return v;
    }
}

/// <summary>
/// Converts provider list prices (published in USD) into the tenant's billing
/// currency. Rates come from configuration — Billing:UsdRates:INR — or from a
/// per-organization override, so a deployment can pin the rate its finance team uses
/// rather than depend on a live FX feed.
/// </summary>
public sealed class BillingOptions
{
    public string DefaultCurrency { get; set; } = "USD";
    /// <summary>Units of the keyed currency per 1 USD, e.g. { "INR": 83.5 }.</summary>
    public Dictionary<string, decimal> UsdRates { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Default customer markup applied when an organization sets none.</summary>
    public int DefaultMarkupBps { get; set; }

    public decimal RateFor(string currency)
    {
        var code = CurrencyInfo.Normalize(currency);
        if (string.Equals(code, "USD", StringComparison.OrdinalIgnoreCase)) return 1m;
        return UsdRates.TryGetValue(code, out var r) && r > 0 ? r : 1m;
    }
}
