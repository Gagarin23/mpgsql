using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Mpgsql.Benchmarks.NpgsqlBaseline;
using Mpgsql.Converters;
using Npgsql.Internal;

namespace Mpgsql.Benchmarks;

[MemoryDiagnoser, WarmupCount(3), IterationCount(8), IterationTime(150), GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory), CategoriesColumn]
public class NpgsqlLongArrayWriteBenchmarks
{
    private ValueMetadata _copiedMetadata;
    private NpgsqlArrayHarness _harness = null!;
    private ReadOnlyMemory<long> _memory;
    private ValueMetadata _originalMetadata;
    private long[] _values = [];
    [Params(0, 1, 8, 256, 4096, 65536)]
    public int Count { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _values = NpgsqlArrayVerification.Values(Count);
        _memory = _values;
        _harness = new NpgsqlArrayHarness
        (
            Math.Max
            (
                20,
                Int64ArrayConverter.GetByteCount(_memory)
            )
        );
        _originalMetadata = _harness.Prepare
        (
            _harness.Original,
            _values
        );
        _copiedMetadata = _harness.Prepare
        (
            _harness.Copy,
            _values
        );
        NpgsqlOriginal();
        byte[] expected = [.. _harness.Output.WrittenSpan];
        Check
        (
            expected,
            NpgsqlCopied()
        );
        Check
        (
            expected,
            NpgsqlOriginalPrepared()
        );
        Check
        (
            expected,
            NpgsqlCopiedPrepared()
        );
        Check
        (
            expected,
            _harness
                .WriteAsync
                (
                    _harness.Original,
                    _values
                )
                .GetAwaiter()
                .GetResult()
        );
        Check
        (
            expected,
            _harness
                .WriteAsync
                (
                    _harness.Copy,
                    _values
                )
                .GetAwaiter()
                .GetResult()
        );
        var size = Mpgsql();
        if (size != Int64ArrayConverter.GetByteCount(Count) ||
            !Int64ArrayConverter
                .Read(_harness.Output.WrittenSpan)
                .Span.SequenceEqual(_values) ||
            Count != 0 && !expected
                .AsSpan()
                .SequenceEqual(_harness.Output.WrittenSpan))
        {
            throw new InvalidOperationException("Mpgsql.Protocol bytes differ from the original Npgsql converter.");
        }
        // Empty arrays are valid with either ndims=0 (12 bytes) or ndims=1, count=0 (20 bytes).
        using var crossRead = new NpgsqlArrayHarness
        (
            20,
            [.. _harness.Output.WrittenSpan]
        );
        if (!crossRead
                .Read(crossRead.Original)
                .AsSpan()
                .SequenceEqual(_values) ||
            !crossRead
                .Read(crossRead.Copy)
                .AsSpan()
                .SequenceEqual(_values))
        {
            throw new InvalidOperationException("Npgsql could not decode Mpgsql.Protocol output.");
        }
    }

    private void Check(
        byte[] expected,
        int size
    )
    {
        if (size != expected.Length || !expected
                .AsSpan()
                .SequenceEqual(_harness.Output.WrittenSpan))
        {
            throw new InvalidOperationException("The copied Npgsql encoder differs from the package converter.");
        }
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SizeAndWrite")]
    public int NpgsqlOriginal()
    {
        return _harness.Write
        (
            _harness.Original,
            _values
        );
    }

    [Benchmark, BenchmarkCategory("SizeAndWrite")]
    public int NpgsqlCopied()
    {
        return _harness.Write
        (
            _harness.Copy,
            _values
        );
    }

    [Benchmark, BenchmarkCategory("SizeAndWrite")]
    public int Mpgsql()
    {
        _ = Int64ArrayConverter.GetByteCount(_memory);
        _harness.Output.Reset();
        Int64ArrayConverter.Write
        (
            _memory,
            _harness.Output
        );
        return _harness.Output.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("PreparedWrite")]
    public int NpgsqlOriginalPrepared()
    {
        return _harness.WritePrepared
        (
            _harness.Original,
            _values,
            _originalMetadata
        );
    }

    [Benchmark, BenchmarkCategory("PreparedWrite")]
    public int NpgsqlCopiedPrepared()
    {
        return _harness.WritePrepared
        (
            _harness.Copy,
            _values,
            _copiedMetadata
        );
    }

    [Benchmark, BenchmarkCategory("PreparedWrite")]
    public int MpgsqlPrepared()
    {
        _harness.Output.Reset();
        Int64ArrayConverter.Write
        (
            _memory,
            _harness.Output
        );
        return _harness.Output.WrittenCount;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        (_originalMetadata.WriteState as IDisposable)?.Dispose();
        (_copiedMetadata.WriteState as IDisposable)?.Dispose();
        _harness?.Dispose();
    }
}