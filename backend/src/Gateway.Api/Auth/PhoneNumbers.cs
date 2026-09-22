using System.Text.RegularExpressions;

namespace Gateway.Api.Auth;

/// <summary>
/// Phone numbers are stored as E.164 digits without '+' (e.g. 919876543210) because
/// the WhatsApp OTP bot builds the chat id as "&lt;digits&gt;@c.us" — a number without
/// its country code is silently undeliverable. Accepts common human input
/// ("+91 98765-43210") and normalises it.
/// </summary>
public static partial class PhoneNumbers
{
    public const string Example = "919876543210";

    /// <summary>Returns the normalised number, or an error message for the caller to show.</summary>
    public static (string? number, string? error) Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (null, null);

        var digits = NonDigits().Replace(raw, "");
        if (raw.TrimStart().StartsWith("00")) digits = digits[2..]; // international prefix

        if (digits.Length is < 8 or > 15 || digits[0] == '0')
            return (null, $"Phone must be the full international number without '+', e.g. {Example}.");

        // A bare 10-digit national number is the usual mistake (India, US, ...): the
        // bot would address "<10 digits>@c.us", which no WhatsApp account matches.
        if (digits.Length == 10)
            return (null, $"Include the country code, e.g. {Example} (not just the 10-digit number).");

        return (digits, null);
    }

    [GeneratedRegex(@"[^0-9]")]
    private static partial Regex NonDigits();
}
