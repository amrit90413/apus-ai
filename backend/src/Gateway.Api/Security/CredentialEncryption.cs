using System.Security.Cryptography;

namespace Gateway.Api.Security;

/// <summary>
/// Supplies the AES-256 data keys used for secrets at rest, keyed by version.
///
/// The shipped implementation reads them from configuration, which in production is
/// a Kubernetes secret / AWS Secrets Manager / Key Vault projection — the master key
/// never lives in the database. Swapping in a KMS-backed envelope scheme means
/// implementing this one interface (fetch + unwrap the data key per version) and
/// registering it instead; nothing else in the gateway changes.
/// </summary>
public interface IDataKeyProvider
{
    /// <summary>Version new ciphertext is written under.</summary>
    int CurrentVersion { get; }

    /// <summary>32-byte key for a version, or null when that version is not available.</summary>
    byte[]? KeyFor(int version);

    IReadOnlyCollection<int> KnownVersions { get; }
}

public sealed class ConfiguredDataKeyProvider : IDataKeyProvider
{
    private readonly Dictionary<int, byte[]> _keys = new();

    public ConfiguredDataKeyProvider(EncryptionOptions opt, IConfiguration config, ILogger<ConfiguredDataKeyProvider> log)
    {
        // Version 1 is the key the pre-003 SecretProtector used, so existing rows
        // (written before encryption_key_version existed, defaulting to 1) decrypt.
        _keys[1] = ResolveLegacyKey(opt, config, log);

        foreach (var (version, material) in opt.Keys)
        {
            if (!int.TryParse(version, out var v) || v < 1)
                throw new InvalidOperationException($"Encryption:Keys has a non-numeric version '{version}'.");
            var key = Convert.FromBase64String(material);
            if (key.Length != 32)
                throw new InvalidOperationException($"Encryption:Keys:{version} must decode to exactly 32 bytes.");
            _keys[v] = key;
        }

        CurrentVersion = opt.CurrentKeyVersion > 0 ? opt.CurrentKeyVersion : _keys.Keys.Max();
        if (!_keys.ContainsKey(CurrentVersion))
            throw new InvalidOperationException(
                $"Encryption:CurrentKeyVersion is {CurrentVersion} but no key material was supplied for it.");

        if (CurrentVersion > 1)
            log.LogInformation("Credential encryption writing under key version {Version} ({Count} versions loaded).",
                CurrentVersion, _keys.Count);
    }

    public int CurrentVersion { get; }

    public byte[]? KeyFor(int version) => _keys.TryGetValue(version, out var k) ? k : null;

    public IReadOnlyCollection<int> KnownVersions => _keys.Keys;

    private static byte[] ResolveLegacyKey(EncryptionOptions opt, IConfiguration config, ILogger log)
    {
        if (!string.IsNullOrWhiteSpace(opt.DataKey))
        {
            var key = Convert.FromBase64String(opt.DataKey);
            if (key.Length != 32)
                throw new InvalidOperationException("Encryption:DataKey must decode to exactly 32 bytes.");
            return key;
        }

        var src = config["Jwt:SigningKey"] ?? "fallback-enc-key-change-in-production";
        log.LogWarning("Encryption:DataKey is unset — deriving the v1 at-rest key from Jwt:SigningKey. " +
                       "Set a dedicated key in production so a JWT rotation cannot brick stored secrets.");
        return SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(src));
    }
}

/// <summary>A secret plus the key version it was encrypted under.</summary>
public readonly record struct SealedSecret(string Ciphertext, int KeyVersion);

/// <summary>
/// Encrypts and decrypts provider credentials. Every value is AES-256-GCM sealed
/// under a versioned data key so keys can be rotated without downtime: new writes use
/// the current version, old rows keep decrypting under theirs, and
/// <see cref="RotateAsync"/> re-seals them in the background.
///
/// Plaintext never leaves a local variable: it is not logged, not put in exception
/// messages, and not serialized into any DTO.
/// </summary>
public interface ICredentialEncryption
{
    int CurrentKeyVersion { get; }
    SealedSecret Encrypt(string plaintext);
    string Decrypt(string ciphertext, int keyVersion);
    /// <summary>Re-seals a value under the current key. Returns null when it is already current.</summary>
    SealedSecret? Rotate(string ciphertext, int keyVersion);
}

public sealed class CredentialEncryption : ICredentialEncryption
{
    private readonly IDataKeyProvider _keys;
    private readonly Dictionary<int, SecretProtector> _protectors = new();

    public CredentialEncryption(IDataKeyProvider keys)
    {
        _keys = keys;
        foreach (var v in keys.KnownVersions)
            _protectors[v] = new SecretProtector(keys.KeyFor(v)!);
    }

    public int CurrentKeyVersion => _keys.CurrentVersion;

    public SealedSecret Encrypt(string plaintext) =>
        new(Protector(_keys.CurrentVersion).Protect(plaintext), _keys.CurrentVersion);

    public string Decrypt(string ciphertext, int keyVersion) =>
        Protector(keyVersion <= 0 ? 1 : keyVersion).Unprotect(ciphertext);

    public SealedSecret? Rotate(string ciphertext, int keyVersion)
    {
        if (keyVersion == _keys.CurrentVersion) return null;
        return Encrypt(Decrypt(ciphertext, keyVersion));
    }

    private SecretProtector Protector(int version) =>
        _protectors.TryGetValue(version, out var p)
            ? p
            // Deliberately vague: the message reaches logs and API responses.
            : throw new CryptographicException($"No data key is loaded for encryption key version {version}.");
}
