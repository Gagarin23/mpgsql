using System.Buffers;
using Mpgsql.Protocol;

namespace Mpgsql.Tests.Protocol;

internal static class TestWire
{
    internal static byte[] Bytes(string hex) => Convert.FromHexString(hex.Replace(" ", "", StringComparison.Ordinal));

    internal static BackendMessage Read(string hex, bool fragmented = true)
    {
        var bytes = Bytes(hex);
        var sequence = fragmented ? ByteSegments(bytes) : new ReadOnlySequence<byte>(bytes);
        Assert.True(BackendMessageReader.TryRead(ref sequence, out var message));
        Assert.True(sequence.IsEmpty);
        return message;
    }

    internal static ReadOnlySequence<byte> ByteSegments(byte[] bytes) =>
        Chunks(bytes.Select((_, i) => (ReadOnlyMemory<byte>)bytes.AsMemory(i, 1)).ToArray());

    internal static ReadOnlySequence<byte> Chunks(params ReadOnlyMemory<byte>[] chunks)
    {
        if (chunks.Length == 0)
            return ReadOnlySequence<byte>.Empty;
        var first = new Segment(chunks[0]);
        var last = first;
        for (int i = 1; i < chunks.Length; i++)
            last = last.Append(chunks[i]);
        return new(first, 0, last, last.Memory.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        internal Segment(ReadOnlyMemory<byte> memory) => Memory = memory;

        internal Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}
