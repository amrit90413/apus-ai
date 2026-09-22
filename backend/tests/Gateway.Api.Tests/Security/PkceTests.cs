using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Gateway.Api.Security;

namespace Gateway.Api.Tests.Security;

public sealed class PkceTests
{
    private static readonly Regex Base64Url = new("^[A-Za-z0-9_-]+$", RegexOptions.Compiled);

    private static string IndependentBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public void NewVerifier_is_43_unpadded_base64url_characters()
    {
        for (var i = 0; i < 200; i++)
        {
            var verifier = Pkce.NewVerifier();

            Assert.Equal(43, verifier.Length); // 32 random bytes -> 43 chars once the single '=' is trimmed
            Assert.Matches(Base64Url, verifier);
            Assert.DoesNotContain('=', verifier);
        }
    }

    [Fact]
    public void NewVerifier_is_unique_across_1000_calls()
    {
        var verifiers = Enumerable.Range(0, 1000).Select(_ => Pkce.NewVerifier()).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(1000, verifiers.Count);
    }

    [Fact]
    public void Challenge_is_base64url_of_sha256_over_the_ascii_verifier()
    {
        var verifier = Pkce.NewVerifier();
        var expected = IndependentBase64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        var challenge = Pkce.Challenge(verifier);

        Assert.Equal(expected, challenge);
        Assert.Equal(43, challenge.Length);
        Assert.Matches(Base64Url, challenge);
    }

    [Fact]
    public void Challenge_matches_the_rfc7636_appendix_b_vector()
    {
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        const string challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

        Assert.Equal(challenge, Pkce.Challenge(verifier));
    }

    [Fact]
    public void Challenge_is_deterministic_for_the_same_verifier()
    {
        var verifier = Pkce.NewVerifier();

        Assert.Equal(Pkce.Challenge(verifier), Pkce.Challenge(verifier));
        Assert.NotEqual(Pkce.Challenge(verifier), Pkce.Challenge(Pkce.NewVerifier()));
    }

    [Fact]
    public void NewState_is_unique_across_1000_calls()
    {
        var states = Enumerable.Range(0, 1000).Select(_ => Pkce.NewState()).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(1000, states.Count);
    }

    [Fact]
    public void NewState_is_32_unpadded_base64url_characters()
    {
        var state = Pkce.NewState();

        Assert.Equal(32, state.Length); // 24 random bytes -> exactly 32 chars, no padding needed
        Assert.Matches(Base64Url, state);
    }
}
