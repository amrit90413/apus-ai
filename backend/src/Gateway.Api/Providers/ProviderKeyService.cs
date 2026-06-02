using System.Security.Cryptography;
using System.Text;
using Gateway.Api.Domain;
using Gateway.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Gateway.Api.Providers;

public interface IProviderKeyService
{
    Task<string?> GetActiveKeyAsync(string provider, CancellationToken ct = default);
    Task<Guid> AddKeyAsync(string provider, string plainKey, CancellationToken ct = default);
    Task RemoveKeyAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<ProviderKeyRow>> ListAsync(CancellationToken ct = default);
}

public sealed record ProviderKeyRow(Guid Id, string Provider, string KeyHint, bool IsActive, DateTimeOffset CreatedAt);

/// <summary>
/// Singleton service that stores AI provider keys encrypted in Postgres (AES-256-GCM)
/// and caches the decrypted active key in memory for 5 minutes. Uses IServiceScopeFactory
/// because this singleton outlives the per-request DbContext scope.
/// </summary>
public sealed class ProviderKeyService : IProviderKeyService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMemoryCache _cache;
    private readonly byte[] _encKey;

    public ProviderKeyService(IServiceScopeFactory scopeFactory, IMemoryCache cache, IConfiguration config)
    {
        _scopeFactory = scopeFactory;
        _cache = cache;
        var src = config["Jwt:SigningKey"] ?? "fallback-enc-key-change-in-production";
        _encKey = SHA256.HashData(Encoding.UTF8.GetBytes(src));
    }

    public async Task<string?> GetActiveKeyAsync(string provider, CancellationToken ct = default)
    {
        var cacheKey = $"pk:{provider}";
        if (_cache.TryGetValue(cacheKey, out string? cached)) return cached;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

        var row = await db.ProviderKeys
            .Where(k => k.Provider == provider && k.IsActive)
            .OrderByDescending(k => k.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (row is null) return null;

        var plain = Decrypt(row.EncryptedKey);
        _cache.Set(cacheKey, plain, TimeSpan.FromMinutes(5));
        return plain;
    }

    public async Task<Guid> AddKeyAsync(string provider, string plainKey, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

        var key = new ProviderKey
        {
            Provider = provider,
            EncryptedKey = Encrypt(plainKey),
            KeyHint = plainKey.Length >= 4 ? $"...{plainKey[^4..]}" : "...????",
        };
        db.ProviderKeys.Add(key);
        await db.SaveChangesAsync(ct);

        _cache.Remove($"pk:{provider}");
        return key.Id;
    }

    public async Task RemoveKeyAsync(Guid id, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

        var key = await db.ProviderKeys.FindAsync([id], ct);
        if (key is null) return;

        key.IsActive = false;
        await db.SaveChangesAsync(ct);
        _cache.Remove($"pk:{key.Provider}");
    }

    public async Task<IReadOnlyList<ProviderKeyRow>> ListAsync(CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

        return await db.ProviderKeys
            .OrderByDescending(k => k.CreatedAt)
            .Select(k => new ProviderKeyRow(k.Id, k.Provider, k.KeyHint, k.IsActive, k.CreatedAt))
            .ToListAsync(ct);
    }

    private string Encrypt(string plain)
    {
        var nonce = new byte[AesGcm.NonceByteSizes.MaxSize];
        RandomNumberGenerator.Fill(nonce);
        var plainBytes = Encoding.UTF8.GetBytes(plain);
        var ciphertext = new byte[plainBytes.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];

        using var aes = new AesGcm(_encKey, AesGcm.TagByteSizes.MaxSize);
        aes.Encrypt(nonce, plainBytes, ciphertext, tag);

        var blob = new byte[nonce.Length + tag.Length + ciphertext.Length];
        nonce.CopyTo(blob, 0);
        tag.CopyTo(blob, nonce.Length);
        ciphertext.CopyTo(blob, nonce.Length + tag.Length);
        return Convert.ToBase64String(blob);
    }

    private string Decrypt(string b64)
    {
        var blob = Convert.FromBase64String(b64);
        var nonceSize = AesGcm.NonceByteSizes.MaxSize;
        var tagSize = AesGcm.TagByteSizes.MaxSize;

        var nonce = blob[..nonceSize];
        var tag = blob[nonceSize..(nonceSize + tagSize)];
        var ciphertext = blob[(nonceSize + tagSize)..];
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(_encKey, tagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }
}
