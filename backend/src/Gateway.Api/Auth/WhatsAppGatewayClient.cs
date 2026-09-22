using System.Net.Http.Json;

namespace Gateway.Api.Auth;

public sealed class WhatsAppOptions
{
    public string BotUrl { get; set; } = "http://host.docker.internal:3000";
    public string ApiKey { get; set; } = "";

    /// <summary>
    /// When false, admin logins skip the WhatsApp OTP step and receive a JWT from
    /// the password check alone. Set this only where no OTP bot is reachable —
    /// it removes the second factor from every OrgAdmin and SuperAdmin login.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How long a login may wait for the bot to accept the OTP message.</summary>
    public int SendTimeoutSeconds { get; set; } = 55;
}

public sealed class WhatsAppGatewayClient
{
    private readonly HttpClient _http;
    private readonly WhatsAppOptions _opt;
    private readonly ILogger<WhatsAppGatewayClient> _log;

    public WhatsAppGatewayClient(HttpClient http, WhatsAppOptions opt, ILogger<WhatsAppGatewayClient> log)
    {
        _http = http;
        _opt = opt;
        _log = log;
    }

    /// <summary>Returns true only when the gateway accepted the message.</summary>
    public Task<bool> SendOtpAsync(string phoneNumber, string otp, CancellationToken ct = default)
    {
        var message =
            $"🔐 *YourCompany AI*\n\n" +
            $"Your login OTP is: *{otp}*\n\n" +
            $"Valid for 5 minutes. Do not share this code.";
        return SendAsync(phoneNumber, message, ct);
    }

    /// <summary>
    /// Sends an arbitrary message through the same bot. Used by the notification
    /// worker for allowance and connection alerts, so both paths share one client,
    /// one timeout and one failure story.
    /// </summary>
    public async Task<bool> SendAsync(string phoneNumber, string message, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_opt.BotUrl}/send-message");
        req.Headers.Add("x-api-key", _opt.ApiKey);
        req.Content = JsonContent.Create(new { number = phoneNumber, message });

        try
        {
            var resp = await _http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode) return true;

            _log.LogWarning("WhatsApp send failed: {Status}", resp.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            // Reported to the caller rather than swallowed: the password has already
            // been verified by this point, so there is no enumeration risk in saying
            // delivery failed — and hiding it locks the admin out with a pending
            // token that can never be completed.
            _log.LogError(ex, "WhatsApp gateway unreachable");
            return false;
        }
    }
}
