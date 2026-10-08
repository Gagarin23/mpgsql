using System.Buffers;
using BenchmarkDotNet.Attributes;
using Mpgsql.Benchmarks.NpgsqlBaseline;
using Mpgsql.Converters;

namespace Mpgsql.Benchmarks;

[MemoryDiagnoser, WarmupCount(3), IterationCount(8), IterationTime(150)]
public class NpgsqlLongArrayReadBenchmarks
{
    private NpgsqlArrayHarness _harness = null!;

    private ReadOnlySequence<byte> _payload;
    [Params(0, 1, 8, 256, 4096, 65536)]
    public int Count { get; set; }

    // 0: all bytes already buffered, no input copy. 8192: Npgsql refills its
    // reader buffer from MemoryStream; Mpgsql reads an existing segmented sequence.
    [Params(0, 8192)]
    public int ReaderBufferSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var values = NpgsqlArrayVerification.Values(Count);
        // Independent scalar reference, same bytes for all three decoders.
        var bytes = new byte[Int64ArrayConverter.GetByteCount(Count)];
        Int64ArrayReference.WriteScalar(values,
            bytes);
        _payload = NpgsqlArrayVerification.Sequence(bytes,
            ReaderBufferSize);
        _harness = new NpgsqlArrayHarness(20,
            bytes,
            ReaderBufferSize);
        if (!NpgsqlOriginal().AsSpan().SequenceEqual(values) ||
            !NpgsqlCopied().AsSpan().SequenceEqual(values) ||
            !Mpgsql().Span.SequenceEqual(values) ||
            !_harness.ReadAsync(_harness.Original).GetAwaiter().GetResult().AsSpan().SequenceEqual(values) ||
            !_harness.ReadAsync(_harness.Copy).GetAwaiter().GetResult().AsSpan().SequenceEqual(values))
        {
            throw new InvalidOperationException("An Npgsql array decoder differs from the expected values.");
        }
    }

    // All paths return owned storage. Reusing Mpgsql's output is measured separately
    // by LongArrayReadBenchmarks and is intentionally not used as this baseline.
    [Benchmark(Baseline = true)]
    public long[] NpgsqlOriginal()
    {
        return _harness.Read(_harness.Original);
    }
    [Benchmark]
    public long[] NpgsqlCopied()
    {
        return _harness.Read(_harness.Copy);
    }
    [Benchmark]
    public ReadOnlyMemory<long> Mpgsql()
    {
        return Int64ArrayConverter.Read(_payload);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _harness?.Dispose();
    }
}