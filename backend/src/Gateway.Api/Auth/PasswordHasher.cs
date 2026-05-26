using System.Security.Cryptography;

namespace Gateway.Api.Auth;

/// <summary>
/// PBKDF2 (SHA-256, 600k iterations) password hashing with per-user salt. In a real
/// deployment prefer Argon2id via the Konscious.Security.Cryptography package; PBKDF2
/// is used here to avoid an extra native dependency while staying FIPS-friendly.
/// Format: {iterations}.{saltB64}.{hashB64}
/// </summary>
public static class PasswordHasher
{
    private const int Iterations = 600_000;
    private const int SaltSize = 16;
    private const int KeySize = 32;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(key)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('.');
        if (parts.Length != 3) return false;
        var iterations = int.Parse(parts[0]);
        var salt = Convert.FromBase64String(parts[1]);
        var expected = Convert.FromBase64String(parts[2]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected); // constant-time
    }
}
