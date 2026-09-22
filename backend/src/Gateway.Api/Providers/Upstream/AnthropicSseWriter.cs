using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gateway.Api.Providers.Upstream;

/// <summary>
/// Emits the Anthropic Messages SSE event sequence.
///
/// Adapters for providers with their own streaming protocol (OpenAI, Gemini) translate
/// into this, so every client of the gateway sees one protocol no matter which
/// provider served the request — which is the whole point of routing by model.
/// </summary>
public sealed class AnthropicSseWriter
{
    private readonly IResponseSink _sink;
    private readonly string _model;
    private readonly string _messageId;
    private bool _startSent;
    private bool _blockOpen;
    private int _blockIndex = -1;

    public AnthropicSseWriter(IResponseSink sink, string model)
    {
        _sink = sink;
        _model = model;
        _messageId = "msg_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
    }

    public async Task StartAsync(int inputTokens, CancellationToken ct)
    {
        if (_startSent) return;
        _startSent = true;
        _sink.BeginEventStream();

        var payload = new JsonObject
        {
            ["type"] = "message_start",
            ["message"] = new JsonObject
            {
                ["id"] = _messageId,
                ["type"] = "message",
                ["role"] = "assistant",
                ["model"] = _model,
                ["content"] = new JsonArray(),
                ["stop_reason"] = null,
                ["stop_sequence"] = null,
                ["usage"] = new JsonObject { ["input_tokens"] = inputTokens, ["output_tokens"] = 0 },
            },
        };
        await SendAsync("message_start", payload, ct);
    }

    public async Task TextDeltaAsync(string text, CancellationToken ct)
    {
        if (text.Length == 0) return;
        await OpenTextBlockAsync(ct);
        await SendAsync("content_block_delta", new JsonObject
        {
            ["type"] = "content_block_delta",
            ["index"] = _blockIndex,
            ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = text },
        }, ct);
    }

    /// <summary>Emits a complete tool call as its own content block.</summary>
    public async Task ToolUseAsync(string id, string name, JsonNode? input, CancellationToken ct)
    {
        await CloseBlockAsync(ct);
        _blockIndex++;
        _blockOpen = true;

        await SendAsync("content_block_start", new JsonObject
        {
            ["type"] = "content_block_start",
            ["index"] = _blockIndex,
            ["content_block"] = new JsonObject
            {
                ["type"] = "tool_use",
                ["id"] = id,
                ["name"] = name,
                ["input"] = new JsonObject(),
            },
        }, ct);

        // Anthropic streams tool arguments as partial JSON; the upstreams we translate
        // hand us the finished object, so it goes out as a single delta.
        await SendAsync("content_block_delta", new JsonObject
        {
            ["type"] = "content_block_delta",
            ["index"] = _blockIndex,
            ["delta"] = new JsonObject
            {
                ["type"] = "input_json_delta",
                ["partial_json"] = (input ?? new JsonObject()).ToJsonString(),
            },
        }, ct);

        await CloseBlockAsync(ct);
    }

    public async Task FinishAsync(string stopReason, int outputTokens, CancellationToken ct)
    {
        await StartAsync(0, ct);
        await CloseBlockAsync(ct);

        await SendAsync("message_delta", new JsonObject
        {
            ["type"] = "message_delta",
            ["delta"] = new JsonObject { ["stop_reason"] = stopReason, ["stop_sequence"] = null },
            ["usage"] = new JsonObject { ["output_tokens"] = outputTokens },
        }, ct);

        await SendAsync("message_stop", new JsonObject { ["type"] = "message_stop" }, ct);
    }

    /// <summary>
    /// A failure after the response has begun. The client already has a 200, so the
    /// only honest signal is an error event inside the stream.
    /// </summary>
    public async Task ErrorAsync(string type, string message, CancellationToken ct)
    {
        await SendAsync("error", new JsonObject
        {
            ["type"] = "error",
            ["error"] = new JsonObject { ["type"] = type, ["message"] = message },
        }, ct);
    }

    private async Task OpenTextBlockAsync(CancellationToken ct)
    {
        if (_blockOpen) return;
        _blockIndex++;
        _blockOpen = true;
        await SendAsync("content_block_start", new JsonObject
        {
            ["type"] = "content_block_start",
            ["index"] = _blockIndex,
            ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = "" },
        }, ct);
    }

    private async Task CloseBlockAsync(CancellationToken ct)
    {
        if (!_blockOpen) return;
        _blockOpen = false;
        await SendAsync("content_block_stop", new JsonObject
        {
            ["type"] = "content_block_stop",
            ["index"] = _blockIndex,
        }, ct);
    }

    private async Task SendAsync(string type, JsonNode payload, CancellationToken ct)
    {
        await _sink.WriteAsync(Sse.Event(type, payload), ct);
        await _sink.FlushAsync(ct);
    }

    /// <summary>Builds the non-streaming Anthropic message object from translated parts.</summary>
    public static JsonObject BuildMessage(string model, IEnumerable<JsonNode> contentBlocks, string stopReason, int inputTokens, int outputTokens)
    {
        var content = new JsonArray();
        foreach (var block in contentBlocks) content.Add(block);

        return new JsonObject
        {
            ["id"] = "msg_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant(),
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = model,
            ["content"] = content,
            ["stop_reason"] = stopReason,
            ["stop_sequence"] = null,
            ["usage"] = new JsonObject { ["input_tokens"] = inputTokens, ["output_tokens"] = outputTokens },
        };
    }

    /// <summary>Reads `data:` payloads out of an SSE stream, ignoring comments and `[DONE]`.</summary>
    public static async IAsyncEnumerable<string> ReadSseDataAsync(
        Stream stream, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line["data:".Length..].Trim();
            if (payload.Length == 0 || payload == "[DONE]") continue;
            yield return payload;
        }
    }

    public static JsonObject? TryParse(string json)
    {
        try { return JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return null; }
    }
}
