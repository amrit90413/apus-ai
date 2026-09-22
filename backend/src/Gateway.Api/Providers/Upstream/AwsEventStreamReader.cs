using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Gateway.Api.Providers.Upstream;

/// <summary>One decoded frame from an AWS event stream.</summary>
public sealed record AwsEventFrame(string? EventType, string? ExceptionType, byte[] Payload);

/// <summary>
/// Decoder for the `application/vnd.amazon.eventstream` framing Bedrock's streaming
/// API returns, so the gateway can turn it back into the SSE the client expects.
///
/// Frame layout: total length (4) | headers length (4) | prelude CRC (4) |
/// headers | payload | message CRC (4). Header values are typed; only the string
/// headers (:event-type, :exception-type) matter here, but every type must be skipped
/// correctly to find the payload.
/// </summary>
public static class AwsEventStreamReader
{
    private const int PreludeBytes = 12;
    private const int MessageCrcBytes = 4;
    private const int MaxFrameBytes = 24 * 1024 * 1024;

    public static async IAsyncEnumerable<AwsEventFrame> ReadAsync(
        Stream stream, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var pending = new MemoryStream();

        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            pending.Write(buffer, 0, read);

            while (true)
            {
                var data = pending.GetBuffer();
                var available = (int)pending.Length;
                if (available < PreludeBytes) break;

                var totalLength = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(0, 4));
                if (totalLength is <= 0 or > MaxFrameBytes)
                    throw new InvalidDataException("Malformed AWS event stream frame length.");
                if (available < totalLength) break;

                var frame = Parse(data.AsSpan(0, totalLength));
                if (frame is not null) yield return frame;

                // Shift the remainder down. Frames are small relative to a response, so
                // the copy is cheaper than tracking a ring buffer.
                var remaining = available - totalLength;
                var rest = data.AsSpan(totalLength, remaining).ToArray();
                pending.SetLength(0);
                pending.Write(rest, 0, remaining);
            }
        }
    }

    private static AwsEventFrame? Parse(ReadOnlySpan<byte> frame)
    {
        var headersLength = BinaryPrimitives.ReadInt32BigEndian(frame.Slice(4, 4));
        var headersStart = PreludeBytes;
        var payloadStart = headersStart + headersLength;
        var payloadLength = frame.Length - payloadStart - MessageCrcBytes;
        if (headersLength < 0 || payloadLength < 0) return null;

        string? eventType = null, exceptionType = null;
        var cursor = headersStart;
        while (cursor < payloadStart)
        {
            var nameLength = frame[cursor];
            cursor += 1;
            if (cursor + nameLength > payloadStart) break;
            var name = Encoding.UTF8.GetString(frame.Slice(cursor, nameLength));
            cursor += nameLength;
            if (cursor >= payloadStart) break;

            var valueType = frame[cursor];
            cursor += 1;

            switch (valueType)
            {
                case 0: case 1: break;                     // bool true / false: no value bytes
                case 2: cursor += 1; break;                // byte
                case 3: cursor += 2; break;                // short
                case 4: cursor += 4; break;                // integer
                case 5: cursor += 8; break;                // long
                case 6: case 7:                            // byte array / string
                {
                    if (cursor + 2 > payloadStart) return null;
                    var valueLength = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(cursor, 2));
                    cursor += 2;
                    if (cursor + valueLength > payloadStart) return null;
                    var value = Encoding.UTF8.GetString(frame.Slice(cursor, valueLength));
                    if (name == ":event-type") eventType = value;
                    else if (name == ":exception-type") exceptionType = value;
                    cursor += valueLength;
                    break;
                }
                case 8: cursor += 8; break;                // timestamp
                case 9: cursor += 16; break;               // uuid
                default: return null;                      // unknown type: cannot trust the offsets
            }
        }

        return new AwsEventFrame(eventType, exceptionType, frame.Slice(payloadStart, payloadLength).ToArray());
    }

    /// <summary>
    /// Bedrock wraps each model chunk as {"bytes":"&lt;base64&gt;"}; the decoded bytes are
    /// the provider-native event JSON (for Claude, an Anthropic SSE event).
    /// </summary>
    public static byte[]? UnwrapChunk(byte[] payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("bytes", out var b) && b.ValueKind == JsonValueKind.String)
                return Convert.FromBase64String(b.GetString()!);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return null;
        }
        return null;
    }
}
