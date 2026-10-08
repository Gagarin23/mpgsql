using System.Buffers;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Mpgsql.Converters;

namespace Mpgsql.Benchmarks;

[MemoryDiagnoser, WarmupCount(3), IterationCount(8), IterationTime(150), GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory), CategoriesColumn]
public class Int64ArrayReadBenchmarks
{
    private long[] _destination = [];

    private ReadOnlySequence<byte> _payload;
    [Params(1, 8, 4096, 65536)]
    public int Count { get; set; }
    [Params(0, 7, 4096)]
    public int SegmentSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var values = new long[Count];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = unchecked((long)(0x0123456789abcdefUL * (ulong)(i + 1)));
        }
        var bytes = new byte[Int64ArrayConverter.GetByteCount(values)];
        Int64ArrayReference.WriteScalar(values,
            bytes);
        _payload = SegmentSize == 0
            ? new ReadOnlySequence<byte>(bytes)
            : Segment(bytes,
                SegmentSize);
        _destination = new long[Count];
        if (Scalar() != Count || !_destination.AsSpan().SequenceEqual(values) ||
            FusedSimd() != Count || !_destination.AsSpan().SequenceEqual(values) ||
            !Int64ArrayConverter.Read(_payload).Span.SequenceEqual(values))
        {
            throw new InvalidOperationException("An array decoder differs from the expected values.");
        }
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Reuse")]
    public int Scalar()
    {
        return Int64ArrayReference.ReadScalar(_payload,
            _destination);
    }
    [Benchmark, BenchmarkCategory("Reuse")]
    public int FusedSimd()
    {
        return Int64ArrayConverter.Read(_payload,
            _destination);
    }
    [Benchmark, BenchmarkCategory("Owned")]
    public ReadOnlyMemory<long> Owned()
    {
        return Int64ArrayConverter.Read(_payload);
    }

    public void CheckReusableAllocations()
    {
        long allocated = 0;
        // Measure steady state; one-time runtime/tiering initialization can occur after warmup.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            for (var i = 0; i < 32; i++)
            {
                FusedSimd();
            }
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0;
                 i < Math.Max(16,
                     4096
                     / Math.Max(1,
                         Count));
                 i++)
            {
                FusedSimd();
            }
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            if (allocated == 0)
            {
                return;
            }
        }
        throw new InvalidOperationException($"Reading {Count} elements in {SegmentSize}-byte segments allocated {allocated} bytes.");
    }

    private static ReadOnlySequence<byte> Segment(byte[] bytes,
        int size)
    {
        var first = new SegmentNode(bytes.AsMemory(0,
            Math.Min(size,
                bytes.Length)));
        var last = first;
        for (var start = size; start < bytes.Length; start += size)
        {
            last = last.Append(bytes.AsMemory(start,
                Math.Min(size,
                    bytes.Length - start)));
        }
        return new ReadOnlySequence<byte>(first,
            0,
            last,
            last.Memory.Length);
    }

    private sealed class SegmentNode : ReadOnlySequenceSegment<byte>
    {
        internal SegmentNode(ReadOnlyMemory<byte> memory)
        {
            Memory = memory;
        }
        internal SegmentNode Append(ReadOnlyMemory<byte> memory)
        {
            var node = new SegmentNode(memory) {RunningIndex = RunningIndex + Memory.Length};
            Next = node;
            return node;
        }
    }
}