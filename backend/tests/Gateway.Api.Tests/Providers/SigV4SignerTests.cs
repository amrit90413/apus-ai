using System.Net.Http.Headers;
using System.Text;
using Gateway.Api.Providers.Upstream;

namespace Gateway.Api.Tests.Providers;

/// <summary>
/// Signature Version 4, checked against AWS's own published test vector plus the
/// properties a signer must have. A signing bug is silent — every Bedrock call simply
/// returns 403 — so a known-answer test is worth more here than any amount of mocking.
/// </summary>
public sealed class SigV4SignerTests
{
    private const string AccessKeyId = "AKIDEXAMPLE";
    private const string SecretKey = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY";
    private static readonly DateTimeOffset VectorTime = new(2015, 8, 30, 12, 36, 0, TimeSpan.Zero);

    private static string Authorization(HttpRequestMessage request) =>
        request.Headers.GetValues("Authorization").Single();

    [Fact]
    public async Task Matches_the_aws_get_vanilla_test_vector()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.amazonaws.com/");

        await SigV4Signer.SignAsync(request, "service", "us-east-1", AccessKeyId, SecretKey, null, VectorTime);

        Assert.Equal(
            "AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/us-east-1/service/aws4_request, " +
            "SignedHeaders=host;x-amz-date, " +
            "Signature=5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31",
            Authorization(request));
    }

    [Fact]
    public async Task Matches_the_aws_get_vanilla_query_test_vector()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.amazonaws.com/?Param1=value1");

        await SigV4Signer.SignAsync(request, "service", "us-east-1", AccessKeyId, SecretKey, null, VectorTime);

        Assert.Contains(
            "Signature=a67d582fa61cc504c4bae71f336f98b97f1ea3c7a6bfe1b6e45aec72011b9aeb",
            Authorization(request));
    }

    [Fact]
    public async Task Signs_the_body_so_a_tampered_payload_fails_upstream()
    {
        var first = await SignBedrockAsync("""{"messages":[]}""");
        var second = await SignBedrockAsync("""{"messages":[{"role":"user"}]}""");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task A_body_adds_the_content_hash_and_content_type_to_the_signed_headers()
    {
        using var request = BedrockRequest("""{"a":1}""");

        await SigV4Signer.SignAsync(request, "bedrock", "us-east-1", AccessKeyId, SecretKey, null, VectorTime);

        var authorization = Authorization(request);
        Assert.Contains("content-type", authorization);
        Assert.Contains("x-amz-content-sha256", authorization);
        Assert.True(request.Headers.Contains("x-amz-content-sha256"));
    }

    [Fact]
    public async Task A_session_token_is_signed_when_present()
    {
        var without = await SignBedrockAsync("""{"a":1}""", sessionToken: null);
        var with = await SignBedrockAsync("""{"a":1}""", sessionToken: "FwoGZXIvYXdzE...");

        Assert.DoesNotContain("x-amz-security-token", without);
        Assert.Contains("x-amz-security-token", with);
        Assert.NotEqual(without, with);
    }

    [Fact]
    public async Task The_region_and_service_are_part_of_the_credential_scope()
    {
        using var request = BedrockRequest("""{"a":1}""");

        await SigV4Signer.SignAsync(request, "bedrock", "ap-south-1", AccessKeyId, SecretKey, null, VectorTime);

        Assert.Contains("Credential=AKIDEXAMPLE/20150830/ap-south-1/bedrock/aws4_request", Authorization(request));
    }

    [Fact]
    public async Task Signing_twice_replaces_rather_than_duplicates_the_headers()
    {
        // The retry path re-signs the same message; duplicated x-amz-date headers would
        // make every retry fail.
        using var request = BedrockRequest("""{"a":1}""");

        await SigV4Signer.SignAsync(request, "bedrock", "us-east-1", AccessKeyId, SecretKey, null, VectorTime);
        await SigV4Signer.SignAsync(request, "bedrock", "us-east-1", AccessKeyId, SecretKey, null, VectorTime);

        Assert.Single(request.Headers.GetValues("x-amz-date"));
        Assert.Single(request.Headers.GetValues("host"));
    }

    [Fact]
    public async Task Model_ids_with_dots_and_colons_survive_path_encoding()
    {
        // Bedrock model ids look like anthropic.claude-...-v1:0 — a double-encoded path
        // is the classic way to break this.
        var modelId = Uri.EscapeDataString("anthropic.claude-sonnet-4-5-20250929-v1:0");
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://bedrock-runtime.us-east-1.amazonaws.com/model/{modelId}/invoke")
        {
            Content = new StringContent("{}", Encoding.UTF8),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var exception = await Record.ExceptionAsync(() =>
            SigV4Signer.SignAsync(request, "bedrock", "us-east-1", AccessKeyId, SecretKey, null, VectorTime));

        Assert.Null(exception);
        Assert.Contains("Signature=", Authorization(request));
    }

    private static HttpRequestMessage BedrockRequest(string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            "https://bedrock-runtime.us-east-1.amazonaws.com/model/test-model/invoke")
        {
            Content = new StringContent(body, Encoding.UTF8),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return request;
    }

    private static async Task<string> SignBedrockAsync(string body, string? sessionToken = null)
    {
        using var request = BedrockRequest(body);
        await SigV4Signer.SignAsync(request, "bedrock", "us-east-1", AccessKeyId, SecretKey, sessionToken, VectorTime);
        return Authorization(request);
    }
}
