using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gateway.Api.Billing;

namespace Gateway.Api.Providers.Upstream;

/// <summary>
/// OpenAI Chat Completions, translated to and from the Anthropic Messages protocol the
/// gateway exposes. A tenant can therefore route a request at an OpenAI model from a
/// client that only speaks Anthropic, with quota, allowance and ledger behaviour
/// identical to every other provider.
/// </summary>
public sealed class OpenAiUpstreamAdapter : IUpstreamAdapter
{
    private readonly ILogger<OpenAiUpstreamAdapter> _log;

    public OpenAiUpstreamAdapter(ILogger<OpenAiUpstreamAdapter> log) => _log = log;

    public string Provider => ProviderCatalog.OpenAi;

    // ------------------------------------------------------------------ request

    public Task<HttpRequestMessage> BuildAsync(UpstreamCall call, CancellationToken ct)
    {
        var native = ModelMapping.Resolve(call.Connection, call.CanonicalModel);
        var payload = ToOpenAiRequest(call.Body, native, call.Streaming);

        var req = new HttpRequestMessage(HttpMethod.Post, $"{ProviderEndpoints.RuntimeBase(call.Connection)}/v1/chat/completions")
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(payload)),
        };
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", call.Connection.Secret);

        if (call.Connection.Config1("organization") is { Length: > 0 } org)
            req.Headers.Add("OpenAI-Organization", org);

        return Task.FromResult(req);
    }

    internal static JsonObject ToOpenAiRequest(JsonObject body, string model, bool streaming)
    {
        var messages = new JsonArray();

        if (GeminiTranslation.SystemText(body) is { Length: > 0 } system)
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = system });

        foreach (var message in body["messages"] as JsonArray ?? new JsonArray())
        {
            if (message is not JsonObject m) continue;
            var role = m["role"]?.GetValue<string>() ?? "user";
            foreach (var translated in TranslateMessage(role, m["content"]))
                messages.Add(translated);
        }

        var request = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages,
            ["stream"] = streaming,
        };

        // Newer OpenAI models reject max_tokens in favour of max_completion_tokens;
        // sending both is rejected too, so pick by family.
        if (body["max_tokens"]?.GetValue<int>() is { } maxTokens && maxTokens > 0)
            request[UsesCompletionTokens(model) ? "max_completion_tokens" : "max_tokens"] = maxTokens;

        if (body["temperature"] is JsonValue temp && temp.TryGetValue<double>(out var t)) request["temperature"] = t;
        if (body["top_p"] is JsonValue topP && topP.TryGetValue<double>(out var p)) request["top_p"] = p;
        if (body["stop_sequences"] is JsonArray stops && stops.Count > 0) request["stop"] = (JsonArray)stops.DeepClone();

        if (streaming)
            // Without this OpenAI omits usage from streamed responses entirely, and the
            // gateway would have nothing to reconcile the reservation against.
            request["stream_options"] = new JsonObject { ["include_usage"] = true };

        if (body["tools"] is JsonArray tools && tools.Count > 0)
        {
            var translated = new JsonArray();
            foreach (var tool in tools)
            {
                if (tool is not JsonObject tl) continue;
                var function = new JsonObject { ["name"] = tl["name"]?.GetValue<string>() ?? "tool" };
                if (tl["description"] is { } d) function["description"] = d.DeepClone();
                if (tl["input_schema"] is { } s) function["parameters"] = s.DeepClone();
                translated.Add(new JsonObject { ["type"] = "function", ["function"] = function });
            }
            if (translated.Count > 0) request["tools"] = translated;
        }

        return request;
    }

    private static bool UsesCompletionTokens(string model) =>
        model.StartsWith("o1", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("o3", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("o4", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One Anthropic message can become several OpenAI ones: tool results are their own
    /// `tool` role messages rather than blocks inside a user turn.
    /// </summary>
    private static IEnumerable<JsonObject> TranslateMessage(string role, JsonNode? content)
    {
        if (content is JsonValue v && v.TryGetValue<string>(out var text))
        {
            yield return new JsonObject { ["role"] = role, ["content"] = text };
            yield break;
        }

        var parts = new JsonArray();
        var toolCalls = new JsonArray();
        var toolResults = new List<JsonObject>();

        foreach (var block in content as JsonArray ?? new JsonArray())
        {
            if (block is not JsonObject b) continue;
            switch (b["type"]?.GetValue<string>())
            {
                case "text":
                    if (b["text"]?.GetValue<string>() is { Length: > 0 } t)
                        parts.Add(new JsonObject { ["type"] = "text", ["text"] = t });
                    break;

                case "image":
                    if (b["source"] is JsonObject source && source["data"]?.GetValue<string>() is { } data)
                    {
                        var mediaType = source["media_type"]?.GetValue<string>() ?? "image/png";
                        parts.Add(new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject { ["url"] = $"data:{mediaType};base64,{data}" },
                        });
                    }
                    break;

                case "tool_use":
                    toolCalls.Add(new JsonObject
                    {
                        ["id"] = b["id"]?.GetValue<string>() ?? "call_0",
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = b["name"]?.GetValue<string>() ?? "tool",
                            ["arguments"] = (b["input"] ?? new JsonObject()).ToJsonString(),
                        },
                    });
                    break;

                case "tool_result":
                    toolResults.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = b["tool_use_id"]?.GetValue<string>() ?? "call_0",
                        ["content"] = FlattenText(b["content"]),
                    });
                    break;
            }
        }

        if (parts.Count > 0 || toolCalls.Count > 0)
        {
            var message = new JsonObject { ["role"] = role };
            if (parts.Count > 0) message["content"] = parts;
            else message["content"] = null;
            if (toolCalls.Count > 0) message["tool_calls"] = toolCalls;
            yield return message;
        }

        foreach (var result in toolResults) yield return result;
    }

    private static string FlattenText(JsonNode? content)
    {
        if (content is null) return "";
        if (content is JsonValue v && v.TryGetValue<string>(out var s)) return s;

        var sb = new StringBuilder();
        foreach (var block in content as JsonArray ?? new JsonArray())
            if (block is JsonObject b && b["text"]?.GetValue<string>() is { } t) sb.Append(t);
        return sb.Length > 0 ? sb.ToString() : content.ToJsonString();
    }

    // ----------------------------------------------------------------- response

    public async Task<BilledTokens> RelayAsync(UpstreamCall call, HttpResponseMessage response, IResponseSink sink, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            await BedrockUpstreamAdapter.RelayErrorAsync(response, sink, ct);
            return BilledTokens.None;
        }

        return call.Streaming
            ? await RelayStreamAsync(call, response, sink, ct)
            : await RelayJsonAsync(call, response, sink, ct);
    }

    private async Task<BilledTokens> RelayJsonAsync(UpstreamCall call, HttpResponseMessage response, IResponseSink sink, CancellationToken ct)
    {
        var raw = await response.Content.ReadAsStringAsync(ct);
        var root = AnthropicSseWriter.TryParse(raw);
        if (root is null)
        {
            _log.LogWarning("OpenAI returned an unreadable response body for {Model}", call.CanonicalModel);
            await BedrockUpstreamAdapter.RelayErrorAsync(response, sink, ct);
            return BilledTokens.None;
        }

        var choice = (root["choices"] as JsonArray)?.FirstOrDefault() as JsonObject;
        var message = choice?["message"] as JsonObject;

        var blocks = new List<JsonNode>();
        if (message?["content"]?.GetValue<string>() is { Length: > 0 } text)
            blocks.Add(new JsonObject { ["type"] = "text", ["text"] = text });

        foreach (var call1 in message?["tool_calls"] as JsonArray ?? new JsonArray())
            if (call1 is JsonObject tc)
                blocks.Add(ToolUseBlock(tc));

        var usage = ReadUsage(root);
        var anthropic = AnthropicSseWriter.BuildMessage(
            call.CanonicalModel, blocks,
            MapFinishReason(choice?["finish_reason"]?.GetValue<string>()),
            usage.Input + usage.CachedInput, usage.Output);

        sink.BeginJson(200);
        await sink.WriteAsync(Encoding.UTF8.GetBytes(anthropic.ToJsonString()), ct);
        return usage;
    }

    private async Task<BilledTokens> RelayStreamAsync(UpstreamCall call, HttpResponseMessage response, IResponseSink sink, CancellationToken ct)
    {
        var writer = new AnthropicSseWriter(sink, call.CanonicalModel);
        var usage = BilledTokens.None;
        var stopReason = "end_turn";
        var started = false;

        // OpenAI streams tool arguments in fragments; they are accumulated per index and
        // emitted once complete, because Anthropic's tool_use block carries whole JSON.
        var toolNames = new Dictionary<int, string>();
        var toolIds = new Dictionary<int, string>();
        var toolArgs = new Dictionary<int, StringBuilder>();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await foreach (var data in AnthropicSseWriter.ReadSseDataAsync(stream, ct))
        {
            var chunk = AnthropicSseWriter.TryParse(data);
            if (chunk is null) continue;

            var chunkUsage = ReadUsage(chunk);
            if (chunkUsage.Total > 0) usage = chunkUsage;

            if (!started)
            {
                await writer.StartAsync(chunkUsage.Input + chunkUsage.CachedInput, ct);
                started = true;
            }

            var choice = (chunk["choices"] as JsonArray)?.FirstOrDefault() as JsonObject;
            if (choice is null) continue;

            if (choice["finish_reason"]?.GetValue<string>() is { Length: > 0 } finish)
                stopReason = MapFinishReason(finish);

            var delta = choice["delta"] as JsonObject;
            if (delta?["content"]?.GetValue<string>() is { Length: > 0 } text)
                await writer.TextDeltaAsync(text, ct);

            foreach (var node in delta?["tool_calls"] as JsonArray ?? new JsonArray())
            {
                if (node is not JsonObject tc) continue;
                var index = tc["index"] is JsonValue iv && iv.TryGetValue<int>(out var i) ? i : 0;
                if (tc["id"]?.GetValue<string>() is { Length: > 0 } id) toolIds[index] = id;
                var function = tc["function"] as JsonObject;
                if (function?["name"]?.GetValue<string>() is { Length: > 0 } name) toolNames[index] = name;
                if (function?["arguments"]?.GetValue<string>() is { Length: > 0 } args)
                {
                    if (!toolArgs.TryGetValue(index, out var sb)) toolArgs[index] = sb = new StringBuilder();
                    sb.Append(args);
                }
            }
        }

        if (!started) await writer.StartAsync(usage.Input + usage.CachedInput, ct);

        foreach (var index in toolNames.Keys.OrderBy(k => k))
        {
            var arguments = toolArgs.TryGetValue(index, out var sb) ? sb.ToString() : "{}";
            await writer.ToolUseAsync(
                toolIds.TryGetValue(index, out var id) ? id : $"call_{index}",
                toolNames[index],
                AnthropicSseWriter.TryParse(arguments) ?? new JsonObject(),
                ct);
        }

        await writer.FinishAsync(stopReason, usage.Output, ct);
        return usage;
    }

    private static JsonObject ToolUseBlock(JsonObject toolCall)
    {
        var function = toolCall["function"] as JsonObject;
        var arguments = function?["arguments"]?.GetValue<string>() ?? "{}";
        return new JsonObject
        {
            ["type"] = "tool_use",
            ["id"] = toolCall["id"]?.GetValue<string>() ?? "call_0",
            ["name"] = function?["name"]?.GetValue<string>() ?? "tool",
            ["input"] = AnthropicSseWriter.TryParse(arguments) ?? new JsonObject(),
        };
    }

    internal static BilledTokens ReadUsage(JsonObject root)
    {
        if (root["usage"] is not JsonObject u) return BilledTokens.None;
        var prompt = Int(u["prompt_tokens"]);
        var cached = u["prompt_tokens_details"] is JsonObject details ? Int(details["cached_tokens"]) : 0;
        return new BilledTokens(Math.Max(0, prompt - cached), Int(u["completion_tokens"]), cached, 0);
    }

    private static int Int(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;

    internal static string MapFinishReason(string? finishReason) => finishReason switch
    {
        "length" => "max_tokens",
        "tool_calls" or "function_call" => "tool_use",
        "stop" or null or "" => "end_turn",
        _ => "end_turn",
    };
}
