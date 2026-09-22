using System.Security.Cryptography;
using System.Text;
using Gateway.Api.Security;

namespace Gateway.Api.Tests.Security;

public sealed class SecretProtectorTests
{
    private static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

    [Theory]
    [InlineData("sk-ant-api03-abcdefgh")]
    [InlineData("héllo wörld — 日本語 — 🔐 emoji")]
    [InlineData("")]
    public void Protect_then_Unprotect_round_trips(string plaintext)
    {
        var protector = new SecretProtector(NewKey());

        var blob = protector.Protect(plaintext);

        Assert.NotEqual(plaintext, blob);
        Assert.Equal(plaintext, protector.Unprotect(blob));
    }

    [Fact]
    public void Protect_uses_a_fresh_nonce_so_identical_plaintexts_differ()
    {
        var protector = new SecretProtector(NewKey());

        var first = protector.Protect("same-secret");
        var second = protector.Protect("same-secret");

        Assert.NotEqual(first, second);
        Assert.Equal("same-secret", protector.Unprotect(first));
        Assert.Equal("same-secret", protector.Unprotect(second));
    }

    [Fact]
    public void Blob_is_base64_of_nonce_tag_and_ciphertext()
    {
        var protector = new SecretProtector(NewKey());
        var plaintext = "abc"; // 3 UTF-8 bytes

        var blob = Convert.FromBase64String(protector.Protect(plaintext));

        // 12-byte nonce + 16-byte tag + ciphertext the same length as the plaintext.
        Assert.Equal(12 + 16 + Encoding.UTF8.GetByteCount(plaintext), blob.Length);
    }

    [Fact]
    public void Same_key_bytes_in_a_second_instance_can_decrypt()
    {
        var key = NewKey();
        var blob = new SecretProtector(key).Protect("portable");

        Assert.Equal("portable", new SecretProtector((byte[])key.Clone()).Unprotect(blob));
    }

    [Theory]
    [InlineData(0)]   // first byte of the nonce
    [InlineData(12)]  // first byte of the tag
    [InlineData(-1)]  // last byte of the ciphertext
    public void Tampering_with_any_byte_fails_authentication(int offset)
    {
        var protector = new SecretProtector(NewKey());
        var blob = Convert.FromBase64String(protector.Protect("secret-value"));
        var index = offset < 0 ? blob.Length + offset : offset;
        blob[index] ^= 0x01;
        var tampered = Convert.ToBase64String(blob);

        // AesGcm surfaces a tag mismatch as AuthenticationTagMismatchException, a CryptographicException subtype.
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(tampered));
    }

    [Fact]
    public void A_different_key_cannot_decrypt()
    {
        var blob = new SecretProtector(NewKey()).Protect("secret-value");
        var other = new SecretProtector(NewKey());

        Assert.ThrowsAny<CryptographicException>(() => other.Unprotect(blob));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(64)]
    public void Constructor_rejects_keys_that_are_not_32_bytes(int length)
    {
        var ex = Assert.Throws<ArgumentException>(() => new SecretProtector(new byte[length]));
        Assert.Equal("key", ex.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(12)]
    [InlineData(27)]
    public void Truncated_blob_shorter_than_nonce_plus_tag_is_rejected(int length)
    {
        var protector = new SecretProtector(NewKey());
        var truncated = Convert.ToBase64String(new byte[length]);

        Assert.Throws<CryptographicException>(() => protector.Unprotect(truncated));
    }

    [Fact]
    public void Non_base64_input_throws_FormatException()
    {
        var protector = new SecretProtector(NewKey());

        Assert.Throws<FormatException>(() => protector.Unprotect("not base64!"));
    }

    [Theory]
    [InlineData("sk-ant-api03-abcdefgh", "...efgh")]
    [InlineData("12345678", "...5678")]
    [InlineData("1234567", "...????")]
    [InlineData("", "...????")]
    public void Hint_shows_only_the_last_four_characters_of_long_secrets(string secret, string expected)
    {
        Assert.Equal(expected, SecretProtector.Hint(secret));
    }
}
