using System.Security.Cryptography;
using System.Text;

namespace Gateway.Api.Security;

public sealed class EncryptionOptions
{
    /// <summary>
    /// Base64-encoded 32-byte key used for AES-256-GCM at rest. Generate with
    /// `openssl rand -base64 32`. When unset the key is derived from Jwt:SigningKey
    /// (legacy behaviour) so rows encrypted by earlier versions still decrypt — set a
    /// dedicated key in production so a JWT-key rotation cannot brick stored secrets.
    ///
    /// This is key version 1. Later versions are supplied through <see cref="Keys"/>.
    /// </summary>
    public string? DataKey { get; set; }

    /// <summary>
    /// Additional data keys by version, e.g. Encryption:Keys:2 = "&lt;base64&gt;". Rotation is
    /// non-breaking: add the new key, point CurrentKeyVersion at it, and rows re-seal
    /// themselves as the rotation worker walks them. Keep the old key until every row
    /// reports the new version.
    /// </summary>
    public Dictionary<string, string> Keys { get; set; } = new();

    /// <summary>Version new secrets are written under. 0 = the highest version supplied.</summary>
    public int CurrentKeyVersion { get; set; }
}

public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string ciphertext);
}

/// <summary>
/// AES-256-GCM with a random 96-bit nonce per value. Blob layout (base64):
/// nonce || tag || ciphertext. Authenticated, so a tampered row fails to decrypt
/// instead of yielding garbage that gets sent to a provider.
/// </summary>
public sealed class SecretProtector : ISecretProtector
{
    private readonly byte[] _key;

    public SecretProtector(EncryptionOptions opt, IConfiguration config, ILogger<SecretProtector> log)
    {
        if (!string.IsNullOrWhiteSpace(opt.DataKey))
        {
            _key = Convert.FromBase64String(opt.DataKey);
            if (_key.Length != 32)
                throw new InvalidOperationException("Encryption:DataKey must decode to exactly 32 bytes.");
        }
        else
        {
            var src = config["Jwt:SigningKey"] ?? "fallback-enc-key-change-in-production";
            _key = SHA256.HashData(Encoding.UTF8.GetBytes(src));
            log.LogWarning("Encryption:DataKey is unset — deriving the at-rest key from Jwt:SigningKey. Set a dedicated key in production.");
        }
    }

    /// <summary>Test/tooling constructor with an explicit raw key.</summary>
    public SecretProtector(byte[] key)
    {
        if (key.Length != 32) throw new ArgumentException("Key must be 32 bytes.", nameof(key));
        _key = key;
    }

    public string Protect(string plaintext)
    {
        var nonce = new byte[AesGcm.NonceByteSizes.MaxSize];
        RandomNumberGenerator.Fill(nonce);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plainBytes.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];

        using var aes = new AesGcm(_key, AesGcm.TagByteSizes.MaxSize);
        aes.Encrypt(nonce, plainBytes, ciphertext, tag);

        var blob = new byte[nonce.Length + tag.Length + ciphertext.Length];
        nonce.CopyTo(blob, 0);
        tag.CopyTo(blob, nonce.Length);
        ciphertext.CopyTo(blob, nonce.Length + tag.Length);
        return Convert.ToBase64String(blob);
    }

    public string Unprotect(string ciphertext)
    {
        var blob = Convert.FromBase64String(ciphertext);
        var nonceSize = AesGcm.NonceByteSizes.MaxSize;
        var tagSize = AesGcm.TagByteSizes.MaxSize;
        if (blob.Length < nonceSize + tagSize)
            throw new CryptographicException("Ciphertext is too short.");

        var nonce = blob[..nonceSize];
        var tag = blob[nonceSize..(nonceSize + tagSize)];
        var body = blob[(nonceSize + tagSize)..];
        var plaintext = new byte[body.Length];

        using var aes = new AesGcm(_key, tagSize);
        aes.Decrypt(nonce, body, tag, plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }

    /// <summary>Last four characters for display; never enough to reconstruct the secret.</summary>
    public static string Hint(string secret) =>
        secret.Length >= 8 ? $"...{secret[^4..]}" : "...????";
}
