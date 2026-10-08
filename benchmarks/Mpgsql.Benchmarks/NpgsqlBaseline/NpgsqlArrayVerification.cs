using System.Buffers;

namespace Mpgsql.Benchmarks.NpgsqlBaseline;

internal static class NpgsqlArrayVerification
{
    internal static void Run()
    {
        foreach (var count in new[] {0, 1, 3, 4, 5, 8, 256, 682, 683, 4096, 65536})
        {
            var write = new NpgsqlLongArrayWriteBenchmarks {Count = count};
            try { write.Setup(); }
            finally { write.Cleanup(); }
            foreach (var bufferSize in new[] {0, 4096, 8192})
            {
                var read = new NpgsqlLongArrayReadBenchmarks {Count = count, ReaderBufferSize = bufferSize};
                try { read.Setup(); }
                finally { read.Cleanup(); }
            }
        }
        Console.WriteLine("Npgsql 10.0.3 original/copy: sync/async bytes, prepared state reuse, owned results, cross-decoding and reader-buffer boundaries verified.");
    }

    internal static long[] Values(int count)
    {
        var values = new long[count];
        ReadOnlySpan<long> edges = [long.MinValue, long.MaxValue, -1, 0, 1, 0x0102030405060708, -0x0102030405060708];
        for (var i = 0; i < count; i++)
        {
            values[i] = i < edges.Length ? edges[i] : unchecked((long)(0x0123456789abcdefUL * (ulong)(i + 1)));
        }
        return values;
    }

    internal static ReadOnlySequence<byte> Sequence(byte[] bytes,
        int segmentSize)
    {
        if (segmentSize == 0)
        {
            return new ReadOnlySequence<byte>(bytes);
        }
        var first = new Segment(bytes.AsMemory(0,
            Math.Min(segmentSize,
                bytes.Length)));
        var last = first;
        for (var offset = segmentSize; offset < bytes.Length; offset += segmentSize)
        {
            last = last.Append(bytes.AsMemory(offset,
                Math.Min(segmentSize,
                    bytes.Length - offset)));
        }
        return new ReadOnlySequence<byte>(first,
            0,
            last,
            last.Memory.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        internal Segment(ReadOnlyMemory<byte> memory)
        {
            Memory = memory;
        }

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory) {RunningIndex = RunningIndex + Memory.Length};
            Next = next;
            return next;
        }
    }
}