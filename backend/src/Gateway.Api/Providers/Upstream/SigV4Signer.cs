using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Gateway.Api.Providers.Upstream;

/// <summary>
/// AWS Signature Version 4 for Bedrock calls.
///
/// Implemented directly rather than pulled in with the AWS SDK: the gateway needs
/// exactly one signing operation against one service, and the SDK's credential
/// resolution (env vars, instance profiles, shared config) is the opposite of what a
/// multi-tenant gateway wants — every request must be signed with the *tenant's*
/// stored credential, never the host's ambient one.
/// </summary>
public static class SigV4Signer
{
    private const string Algorithm = "AWS4-HMAC-SHA256";

    /// <summary>
    /// Signs the request in place. The body must already be set and buffered
    /// (ByteArrayContent/StringContent) because SigV4 hashes the payload.
    /// </summary>
    public static async Task SignAsync(
        HttpRequestMessage request, string service, string region,
        string accessKeyId, string secretAccessKey, string? sessionToken,
        DateTimeOffset now, CancellationToken ct = default)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI is required for signing.");

        var amzDate = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var dateStamp = now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        var payload = request.Content is null
            ? Array.Empty<byte>()
            : await request.Content.ReadAsByteArrayAsync(ct);
        var payloadHash = Hex(SHA256.HashData(payload));

        request.Headers.Remove("host");
        request.Headers.Remove("x-amz-date");
        request.Headers.Remove("x-amz-content-sha256");
        request.Headers.Remove("x-amz-security-token");

        request.Headers.TryAddWithoutValidation("host", uri.IdnHost + (uri.IsDefaultPort ? "" : ":" + uri.Port));
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        if (!string.IsNullOrEmpty(sessionToken))
            request.Headers.TryAddWithoutValidation("x-amz-security-token", sessionToken);

        // Sign host, x-amz-* and content-type — the minimum set AWS requires plus what
        // actually affects the request.
        var signed = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, values) in request.Headers)
        {
            var lower = name.ToLowerInvariant();
            if (lower == "host" || lower.StartsWith("x-amz-", StringComparison.Ordinal))
                signed[lower] = string.Join(",", values.Select(v => v.Trim()));
        }
        if (request.Content?.Headers.ContentType is { } contentType)
            signed["content-type"] = contentType.ToString();

        var signedHeaders = string.Join(";", signed.Keys);
        var canonicalHeaders = string.Concat(signed.Select(kv => $"{kv.Key}:{kv.Value}\n"));
        var canonicalQuery = CanonicalQuery(uri);

        var canonicalRequest = string.Join("\n",
            request.Method.Method,
            CanonicalPath(uri),
            canonicalQuery,
            canonicalHeaders,
            signedHeaders,
            payloadHash);

        var credentialScope = $"{dateStamp}/{region}/{service}/aws4_request";
        var stringToSign = string.Join("\n",
            Algorithm, amzDate, credentialScope, Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));

        var signingKey = SigningKey(secretAccessKey, dateStamp, region, service);
        var signature = Hex(HmacSha256(signingKey, stringToSign));

        request.Headers.TryAddWithoutValidation("Authorization",
            $"{Algorithm} Credential={accessKeyId}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}");
    }

    /// <summary>
    /// AWS expects each path segment URI-encoded once (twice for most services, but
    /// not for S3). Bedrock model ids contain dots and colons, which must survive.
    /// </summary>
    private static string CanonicalPath(Uri uri)
    {
        var path = uri.AbsolutePath;
        if (string.IsNullOrEmpty(path)) return "/";
        var segments = path.Split('/');
        return string.Join("/", segments.Select(s => s.Length == 0 ? "" : RfcEncode(Uri.UnescapeDataString(s))));
    }

    private static string CanonicalQuery(Uri uri)
    {
        var query = uri.Query.TrimStart('?');
        if (query.Length == 0) return "";
        var pairs = query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p =>
            {
                var i = p.IndexOf('=');
                var key = i < 0 ? p : p[..i];
                var value = i < 0 ? "" : p[(i + 1)..];
                return (Key: RfcEncode(Uri.UnescapeDataString(key)), Value: RfcEncode(Uri.UnescapeDataString(value)));
            })
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .ThenBy(p => p.Value, StringComparer.Ordinal);
        return string.Join("&", pairs.Select(p => $"{p.Key}={p.Value}"));
    }

    /// <summary>RFC 3986 percent-encoding: Uri.EscapeDataString leaves '!' '*' '(' ')' alone, AWS does not.</summary>
    private static string RfcEncode(string value)
    {
        var sb = new StringBuilder(value.Length * 2);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~') sb.Append(c);
            else sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private static byte[] SigningKey(string secret, string dateStamp, string region, string service)
    {
        var kDate = HmacSha256(Encoding.UTF8.GetBytes("AWS4" + secret), dateStamp);
        var kRegion = HmacSha256(kDate, region);
        var kService = HmacSha256(kRegion, service);
        return HmacSha256(kService, "aws4_request");
    }

    private static byte[] HmacSha256(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
