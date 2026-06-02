using System.Security.Cryptography;
using System.Text;
using Gateway.Api.Domain;
using StackExchange.Redis;

namespace Gateway.Api.Auth;

public sealed class OtpService
{
    private const int OtpLength = 6;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    private readonly IConnectionMultiplexer _redis;
    private readonly WhatsAppGatewayClient _whatsapp;
    private readonly ILogger<OtpService> _log;

    public OtpService(IConnectionMultiplexer redis, WhatsAppGatewayClient whatsapp, ILogger<OtpService> log)
    {
        _redis = redis;
        _whatsapp = whatsapp;
        _log = log;
    }

    /// <summary>
    /// Generates an OTP, stores it in Redis, sends it via WhatsApp.
    /// Returns a pendingToken the client must echo back with the OTP.
    /// </summary>
    public async Task<string> SendOtpAsync(User user, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(user.PhoneNumber))
            throw new InvalidOperationException("User has no phone number configured.");

        var otp = GenerateOtp();
        var pendingToken = Guid.NewGuid().ToString("N");

        var db = _redis.GetDatabase();
        // Value: "{userId}:{hashedOtp}" — hashed so a Redis dump can't be replayed
        await db.StringSetAsync(
            $"otp:{pendingToken}",
            $"{user.Id}:{Hash(otp)}",
            Ttl);

        await _whatsapp.SendOtpAsync(user.PhoneNumber, otp, ct);
        _log.LogInformation("OTP sent to user {UserId} via WhatsApp", user.Id);

        return pendingToken;
    }

    /// <summary>
    /// Verifies the OTP atomically (get + delete). Returns userId on success, Empty on failure.
    /// </summary>
    public async Task<Guid> VerifyOtpAsync(string pendingToken, string otp)
    {
        var db = _redis.GetDatabase();
        var stored = await db.StringGetDeleteAsync($"otp:{pendingToken}");

        if (stored.IsNull) return Guid.Empty;

        var parts = stored.ToString().Split(':', 2);
        if (parts.Length != 2) return Guid.Empty;
        if (!Guid.TryParse(parts[0], out var userId)) return Guid.Empty;

        return parts[1] == Hash(otp) ? userId : Guid.Empty;
    }

    private static string GenerateOtp()
    {
        // Cryptographically random 6-digit code
        var n = RandomNumberGenerator.GetInt32(0, 1_000_000);
        return n.ToString("D6");
    }

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes);
    }
}
