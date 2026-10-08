using System.Buffers;
using System.Buffers.Binary;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Mpgsql.Benchmarks.NpgsqlBaseline;
using Mpgsql.Converters;

namespace Mpgsql.Benchmarks;

[MemoryDiagnoser, WarmupCount(3), IterationCount(8), IterationTime(150), GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory), CategoriesColumn]
public class Int64ConverterBenchmarks
{
    private readonly byte[] _bytes = new byte[8];
    private readonly long? _nullableValue = 0x0102030405060708;
    private readonly long _value = 0x0102030405060708;
    private readonly FixedBufferWriter _writer = new FixedBufferWriter(8);
    private ReadOnlyMemory<byte>? _nullablePayload;
    private ReadOnlySequence<byte> _segmented;

    [GlobalSetup]
    public void Setup()
    {
        BinaryPrimitives.WriteInt64BigEndian(_bytes,
            _value);
        _nullablePayload = _bytes;
        _segmented = NpgsqlArrayVerification.Sequence(_bytes,
            3);
        if (WritePrimitive() != WriteConverter() || WriteNullable() != 8 ||
            ReadPrimitive() != ReadConverter() || ReadNullable() != _value ||
            ReadSegmentedPrimitive() != ReadSegmentedConverter())
        {
            throw new InvalidOperationException("Scalar bigint benchmark results differ.");
        }
        for (var i = 0; i < 64; i++)
        {
            ExerciseAllPaths();
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1024; i++)
        {
            ExerciseAllPaths();
        }
        if (GC.GetAllocatedBytesForCurrentThread() != before)
        {
            throw new InvalidOperationException("Scalar bigint conversion allocated managed memory.");
        }
    }

    private void ExerciseAllPaths()
    {
        WriteConverter();
        WriteNullable();
        ReadConverter();
        ReadNullable();
        ReadSegmentedConverter();
        _writer.Reset();
        Int64Converter.Write(_value,
            _writer);
        _writer.Reset();
        Int64Converter.Write(_nullableValue,
            _writer);
        Int64Converter.Write(null,
            _bytes);
        Int64Converter.Write(null,
            _writer);
        _ = Int64Converter.ReadNullable((ReadOnlyMemory<byte>?)null);
        _ = Int64Converter.ReadNullable((ReadOnlySequence<byte>?)null);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Write")]
    public int WritePrimitive()
    {
        BinaryPrimitives.WriteInt64BigEndian(_bytes,
            _value);
        return 8;
    }
    [Benchmark, BenchmarkCategory("Write")]
    public int WriteConverter()
    {
        return Int64Converter.Write(_value,
            _bytes);
    }
    [Benchmark, BenchmarkCategory("Write")]
    public int WriteNullable()
    {
        return Int64Converter.Write(_nullableValue,
            _bytes);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Read")]
    public long ReadPrimitive()
    {
        return BinaryPrimitives.ReadInt64BigEndian(_bytes);
    }
    [Benchmark, BenchmarkCategory("Read")]
    public long ReadConverter()
    {
        return Int64Converter.Read(_bytes.AsSpan());
    }
    [Benchmark, BenchmarkCategory("Read")]
    public long? ReadNullable()
    {
        return Int64Converter.ReadNullable(_nullablePayload);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SegmentedRead")]
    public long ReadSegmentedPrimitive()
    {
        var reader = new SequenceReader<byte>(_segmented);
        reader.TryReadBigEndian(out long value);
        return value;
    }
    [Benchmark, BenchmarkCategory("SegmentedRead")]
    public long ReadSegmentedConverter()
    {
        return Int64Converter.Read(_segmented);
    }
}