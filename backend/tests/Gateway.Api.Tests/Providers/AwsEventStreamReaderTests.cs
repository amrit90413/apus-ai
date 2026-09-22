using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gateway.Api.Providers.Upstream;

namespace Gateway.Api.Tests.Providers;

/// <summary>
/// The Bedrock streaming decoder. Bedrock does not speak SSE — it frames events in
/// `application/vnd.amazon.eventstream` — so this is what stands between a client and
/// a stream of binary garbage.
/// </summary>
public sealed class AwsEventStreamReaderTests
{
    /// <summary>Builds a frame the way Bedrock does: prelude, string headers, payload, CRC.</summary>
    private static byte[] Frame(string eventType, byte[] payload, string headerName = ":event-type")
    {
        var headers = new List<byte>();
        headers.Add((byte)headerName.Length);
        headers.AddRange(Encoding.UTF8.GetBytes(headerName));
        headers.Add(7);                                     // value type: string
        var value = Encoding.UTF8.GetBytes(eventType);
        var length = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)value.Length);
        headers.AddRange(length);
        headers.AddRange(value);

        var total = 12 + headers.Count + payload.Length + 4;
        var frame = new byte[total];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, 4), total);
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(4, 4), headers.Count);
        // Prelude and message CRCs are not validated by the reader, so any value works.
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(8, 4), 0);
        headers.CopyTo(frame, 12);
        payload.CopyTo(frame, 12 + headers.Count);
        return frame;
    }

    private static byte[] Chunk(string innerJson) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { bytes = Convert.ToBase64String(Encoding.UTF8.GetBytes(innerJson)) }));

    private static async Task<List<AwsEventFrame>> ReadAsync(params byte[][] frames)
    {
        using var stream = new MemoryStream(frames.SelectMany(f => f).ToArray());
        var result = new List<AwsEventFrame>();
        await foreach (var frame in AwsEventStreamReader.ReadAsync(stream, default)) result.Add(frame);
        return result;
    }

    [Fact]
    public async Task Reads_a_single_frame_with_its_event_type()
    {
        var frames = await ReadAsync(Frame("chunk", Chunk("""{"type":"message_start"}""")));

        Assert.Single(frames);
        Assert.Equal("chunk", frames[0].EventType);
    }

    [Fact]
    public async Task Reads_several_frames_from_one_buffer()
    {
        var frames = await ReadAsync(
            Frame("chunk", Chunk("""{"type":"message_start"}""")),
            Frame("chunk", Chunk("""{"type":"content_block_delta"}""")),
            Frame("chunk", Chunk("""{"type":"message_stop"}""")));

        Assert.Equal(3, frames.Count);
    }

    [Fact]
    public async Task Reassembles_a_frame_split_across_reads()
    {
        // The realistic case: TCP hands the frame over in pieces.
        var whole = Frame("chunk", Chunk("""{"type":"message_start"}"""));
        using var stream = new ChunkedStream(whole, chunkSize: 7);

        var frames = new List<AwsEventFrame>();
        await foreach (var frame in AwsEventStreamReader.ReadAsync(stream, default)) frames.Add(frame);

        Assert.Single(frames);
        Assert.Equal("chunk", frames[0].EventType);
    }

    [Fact]
    public async Task Surfaces_an_exception_frame_so_the_stream_can_be_ended_honestly()
    {
        var frames = await ReadAsync(
            Frame("chunk", Chunk("""{"type":"message_start"}""")),
            Frame("ThrottlingException", Encoding.UTF8.GetBytes("{}"), ":exception-type"));

        Assert.Equal(2, frames.Count);
        Assert.Equal("ThrottlingException", frames[1].ExceptionType);
        Assert.Null(frames[1].EventType);
    }

    [Fact]
    public void UnwrapChunk_decodes_the_base64_payload()
    {
        var inner = AwsEventStreamReader.UnwrapChunk(Chunk("""{"type":"message_stop"}"""));

        Assert.NotNull(inner);
        Assert.Equal("""{"type":"message_stop"}""", Encoding.UTF8.GetString(inner!));
    }

    [Fact]
    public void UnwrapChunk_returns_null_for_anything_it_cannot_decode()
    {
        Assert.Null(AwsEventStreamReader.UnwrapChunk(Encoding.UTF8.GetBytes("not json")));
        Assert.Null(AwsEventStreamReader.UnwrapChunk(Encoding.UTF8.GetBytes("""{"bytes":"!!!not base64!!!"}""")));
        Assert.Null(AwsEventStreamReader.UnwrapChunk(Encoding.UTF8.GetBytes("""{"other":"x"}""")));
    }

    [Fact]
    public async Task A_frame_claiming_an_absurd_length_is_rejected_rather_than_allocated()
    {
        var hostile = new byte[12];
        BinaryPrimitives.WriteInt32BigEndian(hostile.AsSpan(0, 4), int.MaxValue);

        using var stream = new MemoryStream(hostile);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in AwsEventStreamReader.ReadAsync(stream, default)) { }
        });
    }

    [Fact]
    public async Task A_truncated_stream_yields_only_the_complete_frames()
    {
        var complete = Frame("chunk", Chunk("""{"type":"message_start"}"""));
        var partial = Frame("chunk", Chunk("""{"type":"message_stop"}"""))[..10];

        using var stream = new MemoryStream(complete.Concat(partial).ToArray());
        var frames = new List<AwsEventFrame>();
        await foreach (var frame in AwsEventStreamReader.ReadAsync(stream, default)) frames.Add(frame);

        Assert.Single(frames);
    }

    /// <summary>Hands bytes over in small pieces, the way a socket does.</summary>
    private sealed class ChunkedStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _chunkSize;
        private int _position;

        public ChunkedStream(byte[] data, int chunkSize) { _data = data; _chunkSize = chunkSize; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var take = Math.Min(Math.Min(_chunkSize, count), _data.Length - _position);
            if (take <= 0) return 0;
            Array.Copy(_data, _position, buffer, offset, take);
            _position += take;
            return take;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var take = Math.Min(Math.Min(_chunkSize, buffer.Length), _data.Length - _position);
            if (take <= 0) return ValueTask.FromResult(0);
            _data.AsSpan(_position, take).CopyTo(buffer.Span);
            _position += take;
            return ValueTask.FromResult(take);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
