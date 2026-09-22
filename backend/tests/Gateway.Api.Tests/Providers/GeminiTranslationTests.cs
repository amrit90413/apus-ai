using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Gateway.Api.Domain;
using Gateway.Api.Providers;
using Gateway.Api.Providers.Upstream;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gateway.Api.Tests.Providers;

/// <summary>Anthropic Messages to Google generateContent and back.</summary>
public sealed class GeminiTranslationTests
{
    private static readonly GeminiTranslation Translation = new(NullLogger<GeminiTranslation>.Instance);
    private static readonly GeminiUpstreamAdapter Adapter = new(Translation);

    private static ResolvedConnection Connection(string provider = ProviderCatalog.Gemini, params (string key, string value)[] config) => new(
        Guid.NewGuid(), Guid.NewGuid(), provider, ConnectionType.ApiKey, ConnectionStatus.Connected,
        "gemini-key", new Dictionary<string, string>(),
        config.ToDictionary(c => c.key, c => c.value, StringComparer.OrdinalIgnoreCase));

    private static UpstreamCall Call(string bodyJson, bool streaming = false, ResolvedConnection? connection = null)
    {
        var body = (JsonObject)JsonNode.Parse(bodyJson)!;
        return new UpstreamCall(connection ?? Connection(), body["model"]!.GetValue<string>(), body,
            Encoding.UTF8.GetBytes(bodyJson), streaming, null, Array.Empty<string>());
    }

    // ------------------------------------------------------------------ request

    [Fact]
    public void Assistant_turns_become_the_model_role()
    {
        var body = (JsonObject)JsonNode.Parse("""
            {"model":"gemini-2.5-pro","messages":[
              {"role":"user","content":"Hello"},
              {"role":"assistant","content":"Hi"}]}
            """)!;

        var request = GeminiTranslation.ToGeminiRequest(body);
        var contents = (JsonArray)request["contents"]!;

        Assert.Equal("user", contents[0]!["role"]!.GetValue<string>());
        Assert.Equal("model", contents[1]!["role"]!.GetValue<string>());
        Assert.Equal("Hello", ((JsonArray)contents[0]!["parts"]!)[0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void The_system_prompt_becomes_a_system_instruction_not_a_turn()
    {
        var body = (JsonObject)JsonNode.Parse("""
            {"model":"gemini-2.5-pro","system":"Be brief.","messages":[{"role":"user","content":"Hi"}]}
            """)!;

        var request = GeminiTranslation.ToGeminiRequest(body);

        Assert.Equal("Be brief.", ((JsonArray)request["systemInstruction"]!["parts"]!)[0]!["text"]!.GetValue<string>());
        Assert.Single((JsonArray)request["contents"]!);
    }

    [Fact]
    public void Generation_settings_are_carried_across()
    {
        var body = (JsonObject)JsonNode.Parse("""
            {"model":"gemini-2.5-pro","max_tokens":512,"temperature":0.2,"top_p":0.9,
             "stop_sequences":["END"],"messages":[{"role":"user","content":"Hi"}]}
            """)!;

        var config = GeminiTranslation.ToGeminiRequest(body)["generationConfig"]!;

        Assert.Equal(512, config["maxOutputTokens"]!.GetValue<int>());
        Assert.Equal(0.2, config["temperature"]!.GetValue<double>(), 3);
        Assert.Equal(0.9, config["topP"]!.GetValue<double>(), 3);
        Assert.Equal("END", ((JsonArray)config["stopSequences"]!)[0]!.GetValue<string>());
    }

    [Fact]
    public void Images_become_inline_data_parts()
    {
        var body = (JsonObject)JsonNode.Parse("""
            {"model":"gemini-2.5-pro","messages":[{"role":"user","content":[
              {"type":"image","source":{"type":"base64","media_type":"image/jpeg","data":"Zm9v"}}]}]}
            """)!;

        var part = ((JsonArray)((JsonArray)GeminiTranslation.ToGeminiRequest(body)["contents"]!)[0]!["parts"]!)[0]!;

        Assert.Equal("image/jpeg", part["inlineData"]!["mimeType"]!.GetValue<string>());
        Assert.Equal("Zm9v", part["inlineData"]!["data"]!.GetValue<string>());
    }

    [Fact]
    public void Tools_become_function_declarations_and_calls_become_function_calls()
    {
        var body = (JsonObject)JsonNode.Parse("""
            {"model":"gemini-2.5-pro",
             "tools":[{"name":"lookup","description":"Look up","input_schema":{"type":"object"}}],
             "messages":[
               {"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"lookup","input":{"q":"x"}}]},
               {"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"done"}]}]}
            """)!;

        var request = GeminiTranslation.ToGeminiRequest(body);
        var declarations = (JsonArray)((JsonArray)request["tools"]!)[0]!["functionDeclarations"]!;
        var contents = (JsonArray)request["contents"]!;

        Assert.Equal("lookup", declarations[0]!["name"]!.GetValue<string>());
        Assert.Equal("lookup", ((JsonArray)contents[0]!["parts"]!)[0]!["functionCall"]!["name"]!.GetValue<string>());
        Assert.Equal("done", ((JsonArray)contents[1]!["parts"]!)[0]!["functionResponse"]!["response"]!["result"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_api_key_goes_in_a_header_not_the_query_string()
    {
        // A key in the URL leaks into access logs and proxy traces.
        using var request = await Adapter.BuildAsync(Call("""{"model":"gemini-2.5-pro","messages":[]}"""), default);

        Assert.Equal("gemini-key", request.Headers.GetValues("x-goog-api-key").Single());
        Assert.DoesNotContain("gemini-key", request.RequestUri!.ToString());
    }

    [Fact]
    public async Task Streaming_asks_for_sse_framing()
    {
        using var streaming = await Adapter.BuildAsync(Call("""{"model":"gemini-2.5-pro","messages":[]}""", streaming: true), default);
        using var single = await Adapter.BuildAsync(Call("""{"model":"gemini-2.5-pro","messages":[]}"""), default);

        Assert.Contains(":streamGenerateContent", streaming.RequestUri!.ToString());
        Assert.Contains("alt=sse", streaming.RequestUri.ToString());
        Assert.Contains(":generateContent", single.RequestUri!.ToString());
    }

    // ----------------------------------------------------------------- response

    [Fact]
    public async Task A_json_response_becomes_an_anthropic_message()
    {
        using var response = Json("""
            {"candidates":[{"content":{"parts":[{"text":"Hello"}]},"finishReason":"STOP"}],
             "usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":3}}
            """);
        var sink = new BufferedResponseSink();

        var tokens = await Translation.RelayAsync(Call("""{"model":"gemini-2.5-pro","messages":[]}"""), response, sink, default);

        var message = JsonNode.Parse(sink.Text)!;
        Assert.Equal("Hello", ((JsonArray)message["content"]!)[0]!["text"]!.GetValue<string>());
        Assert.Equal("end_turn", message["stop_reason"]!.GetValue<string>());
        Assert.Equal(10, tokens.Input);
        Assert.Equal(3, tokens.Output);
    }

    [Fact]
    public async Task Cached_context_tokens_are_separated_from_fresh_prompt_tokens()
    {
        using var response = Json("""
            {"candidates":[{"content":{"parts":[{"text":"ok"}]},"finishReason":"STOP"}],
             "usageMetadata":{"promptTokenCount":100,"cachedContentTokenCount":60,"candidatesTokenCount":5}}
            """);

        var tokens = await Translation.RelayAsync(Call("""{"model":"gemini-2.5-pro","messages":[]}"""), response, new BufferedResponseSink(), default);

        Assert.Equal(40, tokens.Input);
        Assert.Equal(60, tokens.CachedInput);
    }

    [Fact]
    public async Task A_streamed_response_becomes_the_anthropic_event_sequence()
    {
        using var response = Sse(
            """{"candidates":[{"content":{"parts":[{"text":"Hel"}]}}],"usageMetadata":{"promptTokenCount":5,"candidatesTokenCount":1}}""",
            """{"candidates":[{"content":{"parts":[{"text":"lo"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":5,"candidatesTokenCount":2}}""");
        var sink = new BufferedResponseSink();

        var tokens = await Translation.RelayAsync(Call("""{"model":"gemini-2.5-pro","messages":[]}""", streaming: true), response, sink, default);

        Assert.Contains("event: message_start", sink.Text);
        Assert.Contains("Hel", sink.Text);
        Assert.Contains("event: message_stop", sink.Text);
        Assert.Equal(5, tokens.Input);
        // Gemini reports cumulative counts; the last chunk is the truth, not the sum.
        Assert.Equal(2, tokens.Output);
    }

    [Fact]
    public async Task A_streamed_function_call_becomes_a_tool_use_block()
    {
        using var response = Sse(
            """{"candidates":[{"content":{"parts":[{"functionCall":{"name":"lookup","args":{"q":"x"}}}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":3,"candidatesTokenCount":4}}""");
        var sink = new BufferedResponseSink();

        await Translation.RelayAsync(Call("""{"model":"gemini-2.5-pro","messages":[]}""", streaming: true), response, sink, default);

        Assert.Contains("\"tool_use\"", sink.Text);
        Assert.Contains("lookup", sink.Text);
    }

    [Theory]
    [InlineData("STOP", "end_turn")]
    [InlineData("MAX_TOKENS", "max_tokens")]
    [InlineData("SAFETY", "end_turn")]
    [InlineData(null, "end_turn")]
    public void Finish_reasons_map_to_anthropic_stop_reasons(string? gemini, string expected) =>
        Assert.Equal(expected, GeminiTranslation.MapStopReason(gemini));

    [Fact]
    public async Task An_upstream_error_is_translated_without_leaking_the_body()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"error":{"message":"project 12345 lacks permission"}}""", Encoding.UTF8, "application/json"),
        };
        var sink = new BufferedResponseSink();

        await Translation.RelayAsync(Call("""{"model":"gemini-2.5-pro","messages":[]}"""), response, sink, default);

        Assert.Equal(403, sink.Status);
        Assert.DoesNotContain("12345", sink.Text);
        Assert.Contains("authentication_error", sink.Text);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Sse(params string[] events)
    {
        var payload = string.Concat(events.Select(e => $"data: {e}\n\n"));
        var content = new StringContent(payload, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }
}
