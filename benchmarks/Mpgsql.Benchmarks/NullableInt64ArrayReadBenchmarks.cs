using System.Buffers;
using BenchmarkDotNet.Attributes;
using Mpgsql.Benchmarks.NpgsqlBaseline;
using Mpgsql.Converters;

namespace Mpgsql.Benchmarks;

[MemoryDiagnoser, WarmupCount(3), IterationCount(8), IterationTime(150)]
public class NullableInt64ArrayReadBenchmarks
{
    private NpgsqlArrayHarness _harness = null!;

    private ReadOnlySequence<byte> _payload;
    private long?[] _storage = [];
    [Params(1, 256, 4096)]
    public int Count { get; set; }
    [Params(0, 50, 100)]
    public int NullPercent { get; set; }
    // Set by --verify to exercise refill boundaries; the timed comparison is fully buffered.
    public int ReaderBufferSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var values = NullableInt64ArrayVerification.Values
        (
            Count,
            NullPercent
        );
        using var write = new NpgsqlArrayHarness
        (
            Math.Max
            (
                20,
                NullableInt64ArrayConverter.GetByteCount(values)
            )
        );
        write.Write
        (
            write.NullableOriginal,
            values
        );
        byte[] bytes = [.. write.Output.WrittenSpan];
        _payload = NpgsqlArrayVerification.Sequence
        (
            bytes,
            ReaderBufferSize
        );
        _harness = new NpgsqlArrayHarness
        (
            20,
            bytes,
            ReaderBufferSize
        );
        _storage = new long?[Count];
        if (!NpgsqlOriginal()
                .AsSpan()
                .SequenceEqual(values) || !MpgsqlOwned()
                .Span.SequenceEqual(values) ||
            MpgsqlReusable() != Count || !_storage
                .AsSpan()
                .SequenceEqual(values))
        {
            throw new InvalidOperationException("Nullable array reading differs from Npgsql 10.0.3.");
        }
        CheckReusableAllocations();
    }

    [Benchmark(Baseline = true)]
    public long?[] NpgsqlOriginal()
    {
        return _harness.Read(_harness.NullableOriginal);
    }
    [Benchmark]
    public ReadOnlyMemory<long?> MpgsqlOwned()
    {
        return NullableInt64ArrayConverter.Read(_payload);
    }
    [Benchmark]
    public int MpgsqlReusable()
    {
        return NullableInt64ArrayConverter.Read
        (
            _payload,
            _storage
        );
    }

    public void CheckReusableAllocations()
    {
        for (var i = 0;
             i < 64;
             i++)
        {
            MpgsqlReusable();
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0;
             i < 64;
             i++)
        {
            MpgsqlReusable();
        }
        if (GC.GetAllocatedBytesForCurrentThread() != before)
        {
            throw new InvalidOperationException("Nullable array reading into reusable storage allocated memory.");
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _harness?.Dispose();
    }
}