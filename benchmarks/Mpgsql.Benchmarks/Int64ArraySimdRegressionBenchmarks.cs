using System.Buffers;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Mpgsql.Converters;
using Mpgsql.Benchmarks.NpgsqlBaseline;

namespace Mpgsql.Benchmarks;

[MemoryDiagnoser]
[WarmupCount(3)]
[IterationCount(8)]
[IterationTime(150)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class Int64ArraySimdRegressionBenchmarks
{
    [Params(0, 1, 3, 4, 5, 8, 9, 256, 4096, 65536)]
    public int Count { get; set; }

    private ReadOnlyMemory<long> _values;
    private byte[] _payload = [];
    private long[] _destination = [];

    [GlobalSetup]
    public void Setup()
    {
        long[] backing = NpgsqlArrayVerification.Values(Count + 2);
        _values = backing.AsMemory(1, Count);
        _payload = new byte[Int64ArraySimdBaseline.GetByteCount(Count) + 2];
        _destination = new long[Count + 2];
        WriteOriginal();
        byte[] expected = _payload.ToArray();
        WriteShared();
        if (!expected.AsSpan().SequenceEqual(_payload))
            throw new InvalidDataException("Shared Int64 SIMD changed wire bytes or surrounding storage.");
        if (ReadOriginal() != Count || !_destination.AsSpan(1, Count).SequenceEqual(_values.Span) ||
            ReadShared() != Count || !_destination.AsSpan(1, Count).SequenceEqual(_values.Span))
            throw new InvalidDataException("Shared Int64 SIMD changed decoded values.");
    }

    [Benchmark(Baseline = true), BenchmarkCategory("WriteSpan")]
    public int WriteOriginal() => Int64ArraySimdBaseline.Write(_values, _payload.AsSpan(1));

    [Benchmark, BenchmarkCategory("WriteSpan")]
    public int WriteShared() => Int64ArrayConverter.Write(_values, _payload.AsSpan(1));

    [Benchmark(Baseline = true), BenchmarkCategory("ReadSpan")]
    public int ReadOriginal() => Int64ArraySimdBaseline.Read(_payload.AsSpan(1, _payload.Length - 2), _destination.AsSpan(1));

    [Benchmark, BenchmarkCategory("ReadSpan")]
    public int ReadShared() => Int64ArrayConverter.Read(_payload.AsSpan(1, _payload.Length - 2), _destination.AsSpan(1));
}

[MemoryDiagnoser]
[WarmupCount(3)]
[IterationCount(8)]
[IterationTime(150)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class Int64ArraySimdRegressionSequenceBenchmarks
{
    [Params(0, 7, 48, 4096)]
    public int SegmentSize { get; set; }

    private ReadOnlySequence<byte> _sequence;
    private long[] _destination = [];

    [GlobalSetup]
    public void Setup()
    {
        long[] values = NpgsqlArrayVerification.Values(4096);
        byte[] payload = new byte[Int64ArraySimdBaseline.GetByteCount(values.Length)];
        Int64ArraySimdBaseline.Write(values, payload);
        _sequence = NpgsqlArrayVerification.Sequence(payload, SegmentSize);
        _destination = new long[values.Length];
        if (ReadOriginal() != values.Length || !_destination.AsSpan().SequenceEqual(values) ||
            ReadShared() != values.Length || !_destination.AsSpan().SequenceEqual(values) ||
            !OwnedOriginal().Span.SequenceEqual(values) || !OwnedShared().Span.SequenceEqual(values))
            throw new InvalidDataException("Shared Int64 SIMD changed sequence decoding.");
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Reuse")]
    public int ReadOriginal() => Int64ArraySimdBaseline.Read(_sequence, _destination);
    [Benchmark, BenchmarkCategory("Reuse")]
    public int ReadShared() => Int64ArrayConverter.Read(_sequence, _destination);
    [Benchmark(Baseline = true), BenchmarkCategory("Owned")]
    public ReadOnlyMemory<long> OwnedOriginal() => Int64ArraySimdBaseline.Read(_sequence);
    [Benchmark, BenchmarkCategory("Owned")]
    public ReadOnlyMemory<long> OwnedShared() => Int64ArrayConverter.Read(_sequence);
}

[MemoryDiagnoser]
[WarmupCount(3)]
[IterationCount(8)]
[IterationTime(150)]
public class Int64ArraySimdRegressionWriterBenchmarks
{
    [Params(1, 4, 4096)]
    public int Count { get; set; }
    private ReadOnlyMemory<long> _values;
    private FixedWriter _writer = null!;

    [GlobalSetup]
    public void Setup()
    {
        _values = NpgsqlArrayVerification.Values(Count);
        _writer = new FixedWriter(Int64ArraySimdBaseline.GetByteCount(Count));
        WriteOriginal();
        byte[] expected = _writer.Bytes.ToArray();
        WriteShared();
        if (!expected.AsSpan().SequenceEqual(_writer.Bytes))
            throw new InvalidDataException("Shared Int64 SIMD changed IBufferWriter output.");
    }

    [Benchmark(Baseline = true)]
    public void WriteOriginal() => Int64ArraySimdBaseline.Write(_values, _writer);
    [Benchmark]
    public void WriteShared() => Int64ArrayConverter.Write(_values, _writer);

    private sealed class FixedWriter(int size) : IBufferWriter<byte>
    {
        internal byte[] Bytes { get; } = new byte[size];
        public void Advance(int count) { }
        public Span<byte> GetSpan(int sizeHint = 0) => Bytes;
        public Memory<byte> GetMemory(int sizeHint = 0) => Bytes;
    }
}
