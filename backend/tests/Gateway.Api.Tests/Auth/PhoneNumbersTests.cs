using Gateway.Api.Auth;

namespace Gateway.Api.Tests.Auth;

public class PhoneNumbersTests
{
    [Theory]
    [InlineData("919876543210", "919876543210")]
    [InlineData("+91 98765 43210", "919876543210")]
    [InlineData("+1 (415) 555-0132", "14155550132")]
    [InlineData("0091-9876543210", "919876543210")]
    public void Normalizes_common_human_input(string raw, string expected)
    {
        var (number, error) = PhoneNumbers.Normalize(raw);
        Assert.Null(error);
        Assert.Equal(expected, number);
    }

    [Fact]
    public void Empty_is_allowed_and_yields_null()
    {
        Assert.Equal((null, null), PhoneNumbers.Normalize("  "));
        Assert.Equal((null, null), PhoneNumbers.Normalize(null));
    }

    [Theory]
    [InlineData("8299999975")]   // the production incident: 10 digits, no country code
    [InlineData("98765 43210")]
    public void Ten_digit_national_number_is_rejected_with_country_code_hint(string raw)
    {
        var (number, error) = PhoneNumbers.Normalize(raw);
        Assert.Null(number);
        Assert.Contains("country code", error);
    }

    [Theory]
    [InlineData("1234567")]          // too short
    [InlineData("1234567890123456")] // 16 digits
    [InlineData("09876543210")]      // leading zero (trunk prefix)
    [InlineData("abc")]
    public void Malformed_numbers_are_rejected(string raw)
    {
        var (number, error) = PhoneNumbers.Normalize(raw);
        Assert.Null(number);
        Assert.NotNull(error);
    }
}
