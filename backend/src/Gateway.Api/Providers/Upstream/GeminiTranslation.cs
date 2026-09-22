using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gateway.Api.Billing;

namespace Gateway.Api.Providers.Upstream;

/// <summary>
/// Translates between the Anthropic Messages protocol the gateway exposes and Google's
/// generateContent protocol, so a tenant can point Claude Code at a Gemini model
/// without the client knowing anything changed.
///
/// Shared by the direct Gemini adapter and by Vertex, which serves the same protocol
/// under a different URL and credential.
/// </summary>
public sealed class GeminiTranslation
{
    private readonly ILogger<GeminiTranslation> _log;

    public GeminiTranslation(ILogger<GeminiTranslation> log) => _log = log;

    // ------------------------------------------------------------------ request

    public static HttpContent BuildRequestContent(JsonObject anthropicBody)
    {
        var payload = ToGeminiRequest(anthropicBody);
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(payload));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return content;
    }

    internal static JsonObject ToGeminiRequest(JsonObject body)
    {
        var contents = new JsonArray();

        foreach (var message in body["messages"] as JsonArray ?? new JsonArray())
        {
            if (message is not JsonObject m) continue;
            var role = m["role"]?.GetValue<string>() ?? "user";
            // Gemini calls the assistant "model" and has no system role in `contents`.
            var geminiRole = role == "assistant" ? "model" : "user";

            var parts = TranslateParts(m["content"]);
            if (parts.Count == 0) continue;
            contents.Add(new JsonObject { ["role"] = geminiRole, ["parts"] = parts });
        }

        var request = new JsonObject { ["contents"] = contents };

        if (SystemText(body) is { Length: > 0 } system)
            request["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject { ["text"] = system }),
            };

        var generation = new JsonObject();
        if (body["max_tokens"]?.GetValue<int>() is { } maxTokens && maxTokens > 0) generation["maxOutputTokens"] = maxTokens;
        if (TryDouble(body["temperature"], out var temperature)) generation["temperature"] = temperature;
        if (TryDouble(body["top_p"], out var topP)) generation["topP"] = topP;
        if (body["stop_sequences"] is JsonArray stops && stops.Count > 0)
            generation["stopSequences"] = (JsonArray)stops.DeepClone();
        if (generation.Count > 0) request["generationConfig"] = generation;

        if (body["tools"] is JsonArray tools && tools.Count > 0)
        {
            var declarations = new JsonArray();
            foreach (var tool in tools)
            {
                if (tool is not JsonObject t) continue;
                var declaration = new JsonObject
                {
                    ["name"] = t["name"]?.GetValue<string>() ?? "tool",
                };
                if (t["description"] is { } description) declaration["description"] = description.DeepClone();
                if (t["input_schema"] is { } schema) declaration["parameters"] = schema.DeepClone();
                declarations.Add(declaration);
            }
            if (declarations.Count > 0)
                request["tools"] = new JsonArray(new JsonObject { ["functionDeclarations"] = declarations });
        }

        return request;
    }

    /// <summary>Anthropic allows the system prompt as a string or as an array of text blocks.</summary>
    internal static string SystemText(JsonObject body)
    {
        var system = body["system"];
        if (system is null) return "";
        if (system is JsonValue value && value.TryGetValue<string>(out var text)) return text;

        var sb = new StringBuilder();
        foreach (var block in system as JsonArray ?? new JsonArray())
            if (block is JsonObject b && b["text"]?.GetValue<string>() is { } t)
                sb.Append(sb.Length > 0 ? "\n\n" : "").Append(t);
        return sb.ToString();
    }

    /// <summary>Anthropic content is a string or a list of typed blocks; Gemini wants typed parts.</summary>
    private static JsonArray TranslateParts(JsonNode? content)
    {
        var parts = new JsonArray();
        if (content is null) return parts;

        if (content is JsonValue v && v.TryGetValue<string>(out var text))
        {
            if (text.Length > 0) parts.Add(new JsonObject { ["text"] = text });
            return parts;
        }

        foreach (var block in content as JsonArray ?? new JsonArray())
        {
            if (block is not JsonObject b) continue;
            switch (b["type"]?.GetValue<string>())
            {
                case "text":
                    if (b["text"]?.GetValue<string>() is { Length: > 0 } t)
                        parts.Add(new JsonObject { ["text"] = t });
                    break;

                case "image":
                    if (b["source"] is JsonObject source &&
                        source["type"]?.GetValue<string>() == "base64" &&
                        source["data"]?.GetValue<string>() is { } data)
                    {
                        parts.Add(new JsonObject
                        {
                            ["inlineData"] = new JsonObject
                            {
                                ["mimeType"] = source["media_type"]?.GetValue<string>() ?? "image/png",
                                ["data"] = data,
                            },
                        });
                    }
                    break;

                case "tool_use":
                    parts.Add(new JsonObject
                    {
                        ["functionCall"] = new JsonObject
                        {
                            ["name"] = b["name"]?.GetValue<string>() ?? "tool",
                            ["args"] = b["input"]?.DeepClone() ?? new JsonObject(),
                        },
                    });
                    break;

                case "tool_result":
                    parts.Add(new JsonObject
                    {
                        ["functionResponse"] = new JsonObject
                        {
                            ["name"] = b["tool_use_id"]?.GetValue<string>() ?? "tool",
                            ["response"] = new JsonObject { ["result"] = FlattenResult(b["content"]) },
                        },
                    });
                    break;
            }
        }
        return parts;
    }

    private static string FlattenResult(JsonNode? content)
    {
        if (content is null) return "";
        if (content is JsonValue v && v.TryGetValue<string>(out var s)) return s;

        var sb = new StringBuilder();
        foreach (var block in content as JsonArray ?? new JsonArray())
            if (block is JsonObject b && b["text"]?.GetValue<string>() is { } t)
                sb.Append(t);
        return sb.Length > 0 ? sb.ToString() : content.ToJsonString();
    }

    private static bool TryDouble(JsonNode? node, out double value)
    {
        value = 0;
        return node is JsonValue v && v.TryGetValue(out value);
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
            _log.LogWarning("Gemini returned an unreadable response body for {Model}", call.CanonicalModel);
            await BedrockUpstreamAdapter.RelayErrorAsync(response, sink, ct);
            return BilledTokens.None;
        }

        var candidate = (root["candidates"] as JsonArray)?.FirstOrDefault() as JsonObject;
        var blocks = new List<JsonNode>();
        foreach (var part in (candidate?["content"] as JsonObject)?["parts"] as JsonArray ?? new JsonArray())
        {
            if (part is not JsonObject p) continue;
            if (p["text"]?.GetValue<string>() is { Length: > 0 } text)
                blocks.Add(new JsonObject { ["type"] = "text", ["text"] = text });
            else if (p["functionCall"] is JsonObject fc)
                blocks.Add(ToolUseBlock(fc));
        }

        var usage = ReadUsage(root);
        var message = AnthropicSseWriter.BuildMessage(
            call.CanonicalModel, blocks,
            MapStopReason(candidate?["finishReason"]?.GetValue<string>()),
            usage.Input + usage.CachedInput, usage.Output);

        sink.BeginJson(200);
        await sink.WriteAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), ct);
        return usage;
    }

    private async Task<BilledTokens> RelayStreamAsync(UpstreamCall call, HttpResponseMessage response, IResponseSink sink, CancellationToken ct)
    {
        var writer = new AnthropicSseWriter(sink, call.CanonicalModel);
        var usage = BilledTokens.None;
        var stopReason = "end_turn";
        var started = false;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await foreach (var data in AnthropicSseWriter.ReadSseDataAsync(stream, ct))
        {
            var chunk = AnthropicSseWriter.TryParse(data);
            if (chunk is null) continue;

            var chunkUsage = ReadUsage(chunk);
            // Gemini reports cumulative counts on every chunk, so the last one wins.
            if (chunkUsage.Total > 0) usage = chunkUsage;

            if (!started)
            {
                await writer.StartAsync(chunkUsage.Input + chunkUsage.CachedInput, ct);
                started = true;
            }

            var candidate = (chunk["candidates"] as JsonArray)?.FirstOrDefault() as JsonObject;
            if (candidate?["finishReason"]?.GetValue<string>() is { Length: > 0 } finish)
                stopReason = MapStopReason(finish);

            foreach (var part in (candidate?["content"] as JsonObject)?["parts"] as JsonArray ?? new JsonArray())
            {
                if (part is not JsonObject p) continue;
                if (p["text"]?.GetValue<string>() is { Length: > 0 } text)
                    await writer.TextDeltaAsync(text, ct);
                else if (p["functionCall"] is JsonObject fc)
                    await writer.ToolUseAsync(ToolId(), fc["name"]?.GetValue<string>() ?? "tool", fc["args"], ct);
            }
        }

        if (!started) await writer.StartAsync(usage.Input + usage.CachedInput, ct);
        await writer.FinishAsync(stopReason, usage.Output, ct);
        return usage;
    }

    private static JsonObject ToolUseBlock(JsonObject functionCall) => new()
    {
        ["type"] = "tool_use",
        ["id"] = ToolId(),
        ["name"] = functionCall["name"]?.GetValue<string>() ?? "tool",
        ["input"] = functionCall["args"]?.DeepClone() ?? new JsonObject(),
    };

    private static string ToolId() =>
        "toolu_" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(10)).ToLowerInvariant();

    internal static BilledTokens ReadUsage(JsonObject root)
    {
        if (root["usageMetadata"] is not JsonObject u) return BilledTokens.None;
        var prompt = Int(u["promptTokenCount"]);
        var cached = Int(u["cachedContentTokenCount"]);
        return new BilledTokens(Math.Max(0, prompt - cached), Int(u["candidatesTokenCount"]), cached, 0);
    }

    private static int Int(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;

    internal static string MapStopReason(string? finishReason) => finishReason switch
    {
        "MAX_TOKENS" => "max_tokens",
        "STOP" or null or "" => "end_turn",
        // SAFETY / RECITATION / OTHER: the model stopped on its own terms, which is
        // closest to Anthropic's end_turn from a client's point of view.
        _ => "end_turn",
    };
}
