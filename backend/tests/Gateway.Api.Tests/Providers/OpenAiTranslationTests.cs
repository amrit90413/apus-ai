using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Gateway.Api.Domain;
using Gateway.Api.Providers;
using Gateway.Api.Providers.Upstream;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gateway.Api.Tests.Providers;

/// <summary>
/// Anthropic Messages to OpenAI Chat Completions and back.
///
/// The gateway's whole promise is that a client speaking one protocol can reach any
/// provider; these are the tests that make that promise true for OpenAI.
/// </summary>
public sealed class OpenAiTranslationTests
{
    private static readonly OpenAiUpstreamAdapter Adapter = new(NullLogger<OpenAiUpstreamAdapter>.Instance);

    private static ResolvedConnection Connection(params (string key, string value)[] config) => new(
        Guid.NewGuid(), Guid.NewGuid(), ProviderCatalog.OpenAi, ConnectionType.ApiKey,
        ConnectionStatus.Connected, "sk-openai-test",
        new Dictionary<string, string>(),
        config.ToDictionary(c => c.key, c => c.value, StringComparer.OrdinalIgnoreCase));

    private static UpstreamCall Call(string bodyJson, bool streaming = false, ResolvedConnection? connection = null)
    {
        var body = (JsonObject)JsonNode.Parse(bodyJson)!;
        return new UpstreamCall(connection ?? Connection(), body["model"]!.GetValue<string>(), body,
            Encoding.UTF8.GetBytes(bodyJson), streaming, null, Array.Empty<string>());
    }

    // ------------------------------------------------------------------ request

    [Fact]
    public void A_plain_conversation_maps_to_chat_messages()
    {
        var body = (JsonObject)JsonNode.Parse("""
            {"model":"gpt-4o","max_tokens":256,"system":"Be brief.",
             "messages":[{"role":"user","content":"Hello"},{"role":"assistant","content":"Hi"}]}
            """)!;

        var request = OpenAiUpstreamAdapter.ToOpenAiRequest(body, "gpt-4o", streaming: false);
        var messages = (JsonArray)request["messages"]!;

        Assert.Equal("gpt-4o", request["model"]!.GetValue<string>());
        Assert.Equal(256, request["max_tokens"]!.GetValue<int>());
        Assert.Equal(3, messages.Count);
        Assert.Equal("system", messages[0]!["role"]!.GetValue<string>());
        Assert.Equal("Be brief.", messages[0]!["content"]!.GetValue<string>());
        Assert.Equal("user", messages[1]!["role"]!.GetValue<string>());
    }

    [Fact]
    public void A_system_prompt_given_as_blocks_is_joined()
    {
        var body = (JsonObject)JsonNode.Parse("""
            {"model":"gpt-4o","system":[{"type":"text","text":"One."},{"type":"text","text":"Two."}],
             "messages":[{"role":"user","content":"Hi"}]}
            """)!;

        var request = OpenAiUpstreamAdapter.ToOpenAiRequest(body, "gpt-4o", false);

        Assert.Equal("One.\n\nTwo.", ((JsonArray)request["messages"]!)[0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void Newer_model_families_get_max_completion_tokens_instead_of_max_tokens()
    {
        // Sending max_tokens to an o-series model is rejected outright by OpenAI.
        var body = (JsonObject)JsonNode.Parse("""{"model":"o3","max_tokens":100,"messages":[]}""")!;

        var request = OpenAiUpstreamAdapter.ToOpenAiRequest(body, "o3", false);

        Assert.Null(request["max_tokens"]);
        Assert.Equal(100, request["max_completion_tokens"]!.GetValue<int>());
    }

    [Fact]
    public void Streaming_requests_ask_for_usage_so_the_reservation_can_be_settled()
    {
        var body = (JsonObject)JsonNode.Parse("""{"model":"gpt-4o","messages":[]}""")!;

        var request = OpenAiUpstreamAdapter.ToOpenAiRequest(body, "gpt-4o", streaming: true);

        Assert.True(request["stream"]!.GetValue<bool>());
        Assert.True(request["stream_options"]!["include_usage"]!.GetValue<bool>());
    }

    [Fact]
    public void Images_become_data_url_image_parts()
    {
        var body = (JsonObject)JsonNode.Parse("""
            {"model":"gpt-4o","messages":[{"role":"user","content":[
              {"type":"text","text":"What is this?"},
              {"type":"image","source":{"type":"base64","media_type":"image/png","data":"AAAA"}}]}]}
            """)!;

        var request = OpenAiUpstreamAdapter.ToOpenAiRequest(body, "gpt-4o", false);
        var parts = (JsonArray)((JsonArray)request["messages"]!)[0]!["content"]!;

        Assert.Equal("text", parts[0]!["type"]!.GetValue<string>());
        Assert.Equal("image_url", parts[1]!["type"]!.GetValue<string>());
        Assert.Equal("data:image/png;base64,AAAA", parts[1]!["image_url"]!["url"]!.GetValue<string>());
    }

    [Fact]
    public void Tools_and_tool_calls_survive_the_round_trip_into_openai_shape()
    {
        var body = (JsonObject)JsonNode.Parse("""
            {"model":"gpt-4o",
             "tools":[{"name":"get_weather","description":"Weather","input_schema":{"type":"object"}}],
             "messages":[
               {"role":"assistant","content":[{"type":"tool_use","id":"toolu_1","name":"get_weather","input":{"city":"Pune"}}]},
               {"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_1","content":"31C"}]}]}
            """)!;

        var request = OpenAiUpstreamAdapter.ToOpenAiRequest(body, "gpt-4o", false);
        var messages = (JsonArray)request["messages"]!;

        Assert.Equal("function", ((JsonArray)request["tools"]!)[0]!["type"]!.GetValue<string>());
        Assert.Equal("get_weather", ((JsonArray)request["tools"]!)[0]!["function"]!["name"]!.GetValue<string>());

        var assistant = messages[0]!;
        Assert.Equal("toolu_1", ((JsonArray)assistant["tool_calls"]!)[0]!["id"]!.GetValue<string>());
        Assert.Contains("Pune", ((JsonArray)assistant["tool_calls"]!)[0]!["function"]!["arguments"]!.GetValue<string>());

        // The tool result becomes its own `tool` message, as OpenAI requires.
        var toolResult = messages[1]!;
        Assert.Equal("tool", toolResult["role"]!.GetValue<string>());
        Assert.Equal("toolu_1", toolResult["tool_call_id"]!.GetValue<string>());
        Assert.Equal("31C", toolResult["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_request_carries_the_bearer_key_and_the_optional_organization()
    {
        var connection = Connection(("organization", "org-123"));
        using var request = await Adapter.BuildAsync(Call("""{"model":"gpt-4o","messages":[]}""", connection: connection), default);

        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("sk-openai-test", request.Headers.Authorization.Parameter);
        Assert.Equal("org-123", request.Headers.GetValues("OpenAI-Organization").Single());
        Assert.EndsWith("/v1/chat/completions", request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task A_connection_model_map_renames_the_model_upstream()
    {
        var connection = Connection(("modelMap", """{"gpt-4o":"gpt-4o-2024-11-20"}"""));
        using var request = await Adapter.BuildAsync(Call("""{"model":"gpt-4o","messages":[]}""", connection: connection), default);

        var sent = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
        Assert.Equal("gpt-4o-2024-11-20", sent["model"]!.GetValue<string>());
    }

    // ----------------------------------------------------------------- response

    [Fact]
    public async Task A_json_completion_becomes_an_anthropic_message()
    {
        using var response = Json("""
            {"choices":[{"message":{"role":"assistant","content":"Hello there"},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":12,"completion_tokens":4}}
            """);
        var sink = new BufferedResponseSink();

        var tokens = await Adapter.RelayAsync(Call("""{"model":"gpt-4o","messages":[]}"""), response, sink, default);

        var message = JsonNode.Parse(sink.Text)!;
        Assert.Equal("message", message["type"]!.GetValue<string>());
        Assert.Equal("gpt-4o", message["model"]!.GetValue<string>());
        Assert.Equal("Hello there", ((JsonArray)message["content"]!)[0]!["text"]!.GetValue<string>());
        Assert.Equal("end_turn", message["stop_reason"]!.GetValue<string>());
        Assert.Equal(12, tokens.Input);
        Assert.Equal(4, tokens.Output);
    }

    [Fact]
    public async Task Cached_prompt_tokens_are_billed_as_cache_reads_not_as_fresh_input()
    {
        using var response = Json("""
            {"choices":[{"message":{"content":"ok"},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":100,"completion_tokens":5,"prompt_tokens_details":{"cached_tokens":80}}}
            """);

        var tokens = await Adapter.RelayAsync(Call("""{"model":"gpt-4o","messages":[]}"""), response, new BufferedResponseSink(), default);

        Assert.Equal(20, tokens.Input);
        Assert.Equal(80, tokens.CachedInput);
        Assert.Equal(105, tokens.Total);
    }

    [Fact]
    public async Task A_streamed_completion_becomes_the_anthropic_event_sequence()
    {
        using var response = Sse(
            """{"choices":[{"delta":{"content":"Hel"}}],"usage":null}""",
            """{"choices":[{"delta":{"content":"lo"}}],"usage":null}""",
            """{"choices":[{"delta":{},"finish_reason":"stop"}]}""",
            """{"choices":[],"usage":{"prompt_tokens":7,"completion_tokens":2}}""");
        var sink = new BufferedResponseSink();

        var tokens = await Adapter.RelayAsync(Call("""{"model":"gpt-4o","messages":[]}""", streaming: true), response, sink, default);

        var text = sink.Text;
        Assert.Equal("text/event-stream", sink.ContentType);
        Assert.Contains("event: message_start", text);
        Assert.Contains("event: content_block_start", text);
        Assert.Contains("\"text_delta\"", text);
        Assert.Contains("Hel", text);
        Assert.Contains("event: content_block_stop", text);
        Assert.Contains("event: message_delta", text);
        Assert.Contains("event: message_stop", text);
        Assert.Equal(7, tokens.Input);
        Assert.Equal(2, tokens.Output);
    }

    [Fact]
    public async Task Streamed_tool_call_fragments_are_reassembled_into_one_block()
    {
        using var response = Sse(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"get_weather","arguments":"{\"ci"}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"ty\":\"Pune\"}"}}]}}]}""",
            """{"choices":[{"delta":{},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":3,"completion_tokens":9}}""");
        var sink = new BufferedResponseSink();

        await Adapter.RelayAsync(Call("""{"model":"gpt-4o","messages":[]}""", streaming: true), response, sink, default);

        Assert.Contains("\"tool_use\"", sink.Text);
        Assert.Contains("get_weather", sink.Text);
        Assert.Contains("Pune", sink.Text);
        Assert.Contains("\"stop_reason\":\"tool_use\"", sink.Text);
    }

    [Fact]
    public async Task An_upstream_error_is_translated_and_never_relayed_verbatim()
    {
        // OpenAI error bodies name the organization and key prefix.
        using var response = new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"error":{"message":"Incorrect API key sk-abc123 for org-secret"}}""", Encoding.UTF8, "application/json"),
        };
        var sink = new BufferedResponseSink();

        var tokens = await Adapter.RelayAsync(Call("""{"model":"gpt-4o","messages":[]}"""), response, sink, default);

        Assert.Equal(401, sink.Status);
        Assert.DoesNotContain("sk-abc123", sink.Text);
        Assert.DoesNotContain("org-secret", sink.Text);
        Assert.Contains("authentication_error", sink.Text);
        Assert.Equal(0, tokens.Total);
    }

    [Theory]
    [InlineData("stop", "end_turn")]
    [InlineData("length", "max_tokens")]
    [InlineData("tool_calls", "tool_use")]
    [InlineData(null, "end_turn")]
    [InlineData("content_filter", "end_turn")]
    public void Finish_reasons_map_to_anthropic_stop_reasons(string? openAi, string expected) =>
        Assert.Equal(expected, OpenAiUpstreamAdapter.MapFinishReason(openAi));

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Sse(params string[] events)
    {
        var payload = string.Concat(events.Select(e => $"data: {e}\n\n")) + "data: [DONE]\n\n";
        var content = new StringContent(payload, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }
}
