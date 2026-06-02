using System.Net.Http.Json;

namespace Gateway.Api.Auth;

public sealed class WhatsAppOptions
{
    public string BotUrl { get; set; } = "http://host.docker.internal:3000";
    public string ApiKey { get; set; } = "sahil90413";
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

    public async Task SendOtpAsync(string phoneNumber, string otp, CancellationToken ct = default)
    {
        var message =
            $"🔐 *YourCompany AI*\n\n" +
            $"Your login OTP is: *{otp}*\n\n" +
            $"Valid for 5 minutes. Do not share this code.";

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_opt.BotUrl}/send-message");
        req.Headers.Add("x-api-key", _opt.ApiKey);
        req.Content = JsonContent.Create(new { number = phoneNumber, message });

        try
        {
            var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                _log.LogWarning("WhatsApp send failed: {Status}", resp.StatusCode);
        }
        catch (Exception ex)
        {
            // Non-fatal — log and continue; user will see "OTP sent" regardless
            // to prevent phone number enumeration via timing.
            _log.LogError(ex, "WhatsApp gateway unreachable");
        }
    }
}
