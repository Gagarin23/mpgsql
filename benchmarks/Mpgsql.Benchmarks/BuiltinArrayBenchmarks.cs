using System.Buffers;
using System.Buffers.Binary;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Mpgsql.Benchmarks.NpgsqlBaseline;
using Mpgsql.Converters;

namespace Mpgsql.Benchmarks;

[MemoryDiagnoser, WarmupCount(3), IterationCount(6), IterationTime(100), GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory), CategoriesColumn]
public class BuiltinArrayBenchmarks
{
    private byte[] _bytes = [];
    private int[] _scratch = [];
    private ReadOnlySequence<byte> _sequence;
    private int[] _values = [];
    [Params(1, 256, 4096)]
    public int Count { get; set; }
    [Params(0, 7)]
    public int SegmentSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _values = Enumerable
            .Range(0, Count)
            .Select(i => i * 7919 - 1234567)
            .ToArray();
        _scratch = new int[Count];
        _bytes = new byte[Int32ArrayConverter.GetByteCount(_values)];
        WriteReference();
        var reference = _bytes.ToArray();
        WriteConverter();
        if (!reference
                .AsSpan()
                .SequenceEqual(_bytes))
        {
            throw new InvalidOperationException("Array benchmark encoder mismatch.");
        }
        _sequence = SegmentSize == 0 ? new ReadOnlySequence<byte>(_bytes) : NpgsqlArrayVerification.Sequence(_bytes, SegmentSize);
        ReadConverter();
        if (!_values
                .AsSpan()
                .SequenceEqual(_scratch))
        {
            throw new InvalidOperationException("Array benchmark decoder mismatch.");
        }
        ReadReference();
        if (!_values
                .AsSpan()
                .SequenceEqual(_scratch))
        {
            throw new InvalidOperationException("Array reference decoder mismatch.");
        }
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Write")]
    public int WriteReference()
    {
        var size = checked(20 + 8 * _values.Length);
        BinaryPayload.RequireCapacity(size, _bytes.Length);
        BinaryPayload.RequireSeparate(BinaryPayload.StorageBytes(_values.AsSpan()), _bytes);
        BinaryPrimitives.WriteInt32BigEndian(_bytes, 1);
        BinaryPrimitives.WriteInt32BigEndian(_bytes.AsSpan(4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(_bytes.AsSpan(8), 23);
        BinaryPrimitives.WriteInt32BigEndian(_bytes.AsSpan(12), _values.Length);
        BinaryPrimitives.WriteInt32BigEndian(_bytes.AsSpan(16), 1);
        var offset = 20;
        foreach (var value in _values)
        {
            BinaryPrimitives.WriteInt32BigEndian(_bytes.AsSpan(offset), 4);
            BinaryPrimitives.WriteInt32BigEndian(_bytes.AsSpan(offset + 4), value);
            offset += 8;
        }
        return size;
    }

    [Benchmark, BenchmarkCategory("Write")]
    public int WriteConverter()
    {
        return Int32ArrayConverter.Write(_values, _bytes);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Read")]
    public int ReadReference()
    {
        if (_sequence.IsSingleSegment)
        {
            var payload = _sequence.FirstSpan;
            var count = ArrayPayload.ReadHeader(payload, 23, 4, out var headerSize);
            BinaryPayload.RequireCapacity(count, _scratch.Length);
            BinaryPayload.RequireSeparate(payload, BinaryPayload.StorageBytes(_scratch.AsSpan(0, count)));
            var offset = headerSize;
            for (var i = 0;
                 i < count;
                 i++)
            {
                if (BinaryPrimitives.ReadInt32BigEndian(payload[offset..]) != 4)
                {
                    throw new InvalidDataException("Invalid int4 element.");
                }
                _scratch[i] = BinaryPrimitives.ReadInt32BigEndian(payload[(offset + 4)..]);
                offset += 8;
            }
            if (offset != payload.Length)
            {
                throw new InvalidDataException("Trailing bytes.");
            }
            return count;
        }
        var reader = new SequenceReader<byte>(_sequence);
        var length = ArrayPayload.ReadHeader(ref reader, 23, 4);
        BinaryPayload.RequireCapacity(length, _scratch.Length);
        BinaryPayload.RequireSeparate(_sequence, _scratch.AsSpan(0, length));
        for (var i = 0;
             i < length;
             i++)
        {
            if (!reader.TryReadBigEndian(out int prefix) || prefix != 4 || !reader.TryReadBigEndian(out int value))
            {
                throw new InvalidDataException("Invalid int4 element.");
            }
            _scratch[i] = value;
        }
        if (reader.Remaining != 0)
        {
            throw new InvalidDataException("Trailing bytes.");
        }
        return length;
    }

    [Benchmark, BenchmarkCategory("Read")]
    public int ReadConverter()
    {
        return Int32ArrayConverter.Read(_sequence, _scratch);
    }
}