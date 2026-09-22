using System.Security.Cryptography;
using System.Text;

namespace Gateway.Api.Security;

/// <summary>RFC 7636 helpers for the OAuth authorization-code flow.</summary>
public static class Pkce
{
    public static string NewVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string Challenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string NewState() => Base64Url(RandomNumberGenerator.GetBytes(24));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
