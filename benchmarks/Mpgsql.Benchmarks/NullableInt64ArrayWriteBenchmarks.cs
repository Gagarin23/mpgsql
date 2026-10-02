using System.Buffers.Binary;
using BenchmarkDotNet.Attributes;
using Mpgsql.Benchmarks.NpgsqlBaseline;
using Mpgsql.Converters;

namespace Mpgsql.Benchmarks;

[MemoryDiagnoser]
[WarmupCount(3)]
[IterationCount(8)]
[IterationTime(150)]
public class NullableInt64ArrayWriteBenchmarks
{
    [Params(1, 256, 4096)]
    public int Count { get; set; }
    [Params(0, 50, 100)]
    public int NullPercent { get; set; }

    private long?[] _values = [];
    private NpgsqlArrayHarness _harness = null!;

    [GlobalSetup]
    public void Setup()
    {
        _values = NullableInt64ArrayVerification.Values(Count,
            NullPercent);
        _harness = new(Math.Max(20,
            NullableInt64ArrayConverter.GetByteCount(_values)));
        NpgsqlOriginal();
        byte[] expected = [.. _harness.Output.WrittenSpan];
        // Npgsql 10.0.3 always writes flags=0, even for actual NULL elements.
        // PostgreSQL accepts that; Mpgsql writes the canonical flag used by array_send.
        BinaryPrimitives.WriteInt32BigEndian(expected.AsSpan(4),
            _values.Any(v => !v.HasValue)
                ? 1
                : 0);
        Mpgsql();
        if (Count != 0 && !expected.AsSpan().SequenceEqual(_harness.Output.WrittenSpan))
        {
            throw new InvalidOperationException("Nullable array bytes differ from Npgsql 10.0.3.");
        }
        using var read = new NpgsqlArrayHarness(20,
            [.. _harness.Output.WrittenSpan]);
        if (!read.Read(read.NullableOriginal).AsSpan().SequenceEqual(_values))
        {
            throw new InvalidOperationException("Npgsql could not read nullable Mpgsql output.");
        }
        CheckReusableAllocations();
    }

    // Both timed paths include size calculation, output reservation and writing.
    [Benchmark(Baseline = true)]
    public int NpgsqlOriginal() => _harness.Write(_harness.NullableOriginal,
        _values);

    [Benchmark]
    public int Mpgsql()
    {
        _harness.Output.Reset();
        NullableInt64ArrayConverter.Write(_values,
            _harness.Output);
        return _harness.Output.WrittenCount;
    }

    public void CheckReusableAllocations()
    {
        for (int i = 0; i < 64; i++) Mpgsql();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 64; i++) Mpgsql();
        if (GC.GetAllocatedBytesForCurrentThread() != before)
        {
            throw new InvalidOperationException("Nullable array writing allocated memory.");
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _harness?.Dispose();
}