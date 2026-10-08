using System.Buffers;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Mpgsql.Benchmarks.NpgsqlBaseline;
using Mpgsql.Converters;

namespace Mpgsql.Benchmarks;

public enum NumericArrayKind
{
    Int16,
    Int32,
    Float32,
    Float64,
    Oid,
    Money
}

[MemoryDiagnoser, WarmupCount(3), IterationCount(6), IterationTime(100), GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory), CategoriesColumn]
public class NumericArraySimdBenchmarks
{

    private NumericArrayBenchmarkCase _case = null!;
    [Params
    (
        NumericArrayKind.Int16, NumericArrayKind.Int32, NumericArrayKind.Float32,
        NumericArrayKind.Float64, NumericArrayKind.Oid, NumericArrayKind.Money
    )]
    public NumericArrayKind Kind { get; set; }

    [Params(1, 8, 256, 4096)]
    public int Count { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _case = NumericArrayBenchmarkCase.Create(Kind, Count, 0);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Write")]
    public int WriteScalar()
    {
        return _case.WriteScalar();
    }

    [Benchmark, BenchmarkCategory("Write")]
    public int WriteSimd()
    {
        return _case.WriteSimd();
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Read")]
    public int ReadScalar()
    {
        return _case.ReadScalar();
    }

    [Benchmark, BenchmarkCategory("Read")]
    public int ReadSimd()
    {
        return _case.ReadSimd();
    }
}

[MemoryDiagnoser, WarmupCount(3), IterationCount(6), IterationTime(100)]
public class NumericArraySimdSegmentedBenchmarks
{

    private NumericArrayBenchmarkCase _case = null!;
    [Params(NumericArrayKind.Int16, NumericArrayKind.Int32, NumericArrayKind.Float64)]
    public NumericArrayKind Kind { get; set; }

    [Params(7, 4096)]
    public int SegmentSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _case = NumericArrayBenchmarkCase.Create(Kind, 4096, SegmentSize);
    }

    [Benchmark(Baseline = true)]
    public int ReadScalar()
    {
        return _case.ReadScalar();
    }

    [Benchmark]
    public int ReadSimd()
    {
        return _case.ReadSimd();
    }
}

internal abstract class NumericArrayBenchmarkCase
{
    internal abstract int WriteScalar();
    internal abstract int WriteSimd();
    internal abstract int ReadScalar();
    internal abstract int ReadSimd();

    internal static NumericArrayBenchmarkCase Create(
        NumericArrayKind kind, int count,
        int segmentSize
    )
    {
        return kind switch
        {
            NumericArrayKind.Int16   => new Case<short, Int16Codec>(count, segmentSize),
            NumericArrayKind.Int32   => new Case<int, Int32Codec>(count, segmentSize),
            NumericArrayKind.Float32 => new Case<float, Float32Codec>(count, segmentSize),
            NumericArrayKind.Float64 => new Case<double, Float64Codec>(count, segmentSize),
            NumericArrayKind.Oid     => new Case<uint, OidCodec>(count, segmentSize),
            NumericArrayKind.Money   => new Case<long, MoneyCodec>(count, segmentSize),
            _                        => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private sealed class Case<T, TCodec> : NumericArrayBenchmarkCase
        where T : unmanaged where TCodec : struct, IBinaryCodec<T>
    {
        private readonly byte[] _payload;
        private readonly T[] _scratch;
        private readonly ReadOnlySequence<byte> _sequence;
        private readonly T[] _values;

        internal Case(int count, int segmentSize)
        {
            _values = new T[count];
            new Random(42).NextBytes(MemoryMarshal.AsBytes(_values.AsSpan()));
            _scratch = new T[count];
            _payload = new byte[ScalarNumericArrayBaseline<T, TCodec>.Measure(_values)];
            WriteScalar();
            var reference = _payload.ToArray();
            WriteSimd();
            if (!reference
                    .AsSpan()
                    .SequenceEqual(_payload))
            {
                throw new InvalidDataException("SIMD array bytes differ from the original scalar implementation.");
            }
            _sequence = segmentSize == 0 ? new ReadOnlySequence<byte>(_payload) : NpgsqlArrayVerification.Sequence(_payload, segmentSize);
            ReadScalar();
            CheckRead();
            ReadSimd();
            CheckRead();
        }

        private void CheckRead()
        {
            if (!MemoryMarshal
                    .AsBytes(_values.AsSpan())
                    .SequenceEqual(MemoryMarshal.AsBytes(_scratch.AsSpan())))
            {
                throw new InvalidDataException("The numeric array decoder changed value bits.");
            }
        }

        internal override int WriteScalar()
        {
            return ScalarNumericArrayBaseline<T, TCodec>.Write(_values, _payload);
        }
        internal override int WriteSimd()
        {
            return BinaryArray<T, TCodec>.Write(_values, _payload);
        }
        internal override int ReadScalar()
        {
            return ScalarNumericArrayBaseline<T, TCodec>.Read(_sequence, _scratch);
        }
        internal override int ReadSimd()
        {
            return BinaryArray<T, TCodec>.Read(_sequence, _scratch);
        }
    }
}