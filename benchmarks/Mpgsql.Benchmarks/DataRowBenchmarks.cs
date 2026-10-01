#if PROTOCOL_BASELINE
extern alias baseline;
using OriginalReader = baseline::Mpgsql.Protocol.BackendMessageReader;
#endif

using System.Buffers;
using System.Buffers.Binary;
using BenchmarkDotNet.Attributes;
using Mpgsql.Protocol;

namespace Mpgsql.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
[IterationTime(150)]
public class DataRowBenchmarks
{
    [Params(1, 8, 64)]
    public int Columns { get; set; }

    [Params(false, true)]
    public bool Fragmented { get; set; }

    private ReadOnlySequence<byte> _packet;
    private ReadOnlySequence<byte>?[] _storage = [];

    [GlobalSetup]
    public void Setup()
    {
        var output = new ArrayBufferWriter<byte>();
        output.Write(new byte[] { (byte)'D', 0, 0, 0, 0, 0, (byte)Columns });
        for (int i = 0; i < Columns; i++)
        {
            // Mix ordinary, NULL, and empty values; all data is allocated outside timing.
            int length = i % 5 == 4 ? -1 : i % 5 == 3 ? 0 : 8;
            Span<byte> prefix = output.GetSpan(4);
            BinaryPrimitives.WriteInt32BigEndian(prefix, length);
            output.Advance(4);
            if (length > 0)
                output.Write(new byte[length]);
        }
        byte[] bytes = output.WrittenSpan.ToArray();
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(1, 4), bytes.Length - 1);
        _packet = Fragmented ? Segment(bytes, 7) : new ReadOnlySequence<byte>(bytes);
        _storage = new ReadOnlySequence<byte>?[Columns];

        long expected = CurrentUnindexed();
        if (CurrentIndexed() != expected)
            throw new InvalidOperationException("Indexed row consumption differs.");
#if PROTOCOL_BASELINE
        if (Original() != expected)
            throw new InvalidOperationException("Row consumption differs from the baseline.");
#endif
    }

#if PROTOCOL_BASELINE
    [Benchmark(Baseline = true)]
    public long Original()
    {
        var input = _packet;
        if (!OriginalReader.TryRead(ref input, out var message))
            throw new InvalidOperationException();
        long sum = message.GetDataRow().Count;
        foreach (ReadOnlySequence<byte>? value in message.GetDataRow())
            sum += value?.Length ?? -1;
        return sum + input.Length;
    }
#endif

    [Benchmark]
    public long CurrentUnindexed()
    {
        var input = _packet;
        if (!BackendMessageReader.TryRead(ref input, out var message))
            throw new InvalidOperationException();
        long sum = message.GetDataRow().Count;
        foreach (ReadOnlySequence<byte>? value in message.GetDataRow())
            sum += value?.Length ?? -1;
        return sum + input.Length;
    }

    [Benchmark]
    public long CurrentIndexed()
    {
        var input = _packet;
        if (!BackendMessageReader.TryRead(ref input, _storage, out _, out var row))
            throw new InvalidOperationException();
        long sum = row.Count;
        foreach (ReadOnlySequence<byte>? value in row)
            sum += value?.Length ?? -1;
        return sum + input.Length;
    }

    private static ReadOnlySequence<byte> Segment(byte[] bytes, int size)
    {
        var first = new SegmentNode(bytes.AsMemory(0, Math.Min(size, bytes.Length)));
        var last = first;
        for (int start = size; start < bytes.Length; start += size)
            last = last.Append(bytes.AsMemory(start, Math.Min(size, bytes.Length - start)));
        return new(first, 0, last, last.Memory.Length);
    }

    private sealed class SegmentNode : ReadOnlySequenceSegment<byte>
    {
        public SegmentNode(ReadOnlyMemory<byte> memory) => Memory = memory;

        public SegmentNode Append(ReadOnlyMemory<byte> memory)
        {
            var next = new SegmentNode(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}
