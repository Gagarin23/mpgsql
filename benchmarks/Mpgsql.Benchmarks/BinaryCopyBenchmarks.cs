using System.Buffers;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Mpgsql.Benchmarks.NpgsqlBaseline;
using Mpgsql.Copy;
using Mpgsql.Protocol;

namespace Mpgsql.Benchmarks;

[MemoryDiagnoser, WarmupCount(6), IterationCount(12), IterationTime(500), GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory), CategoriesColumn]
public class BinaryCopyBenchmarks
{
    private readonly ReadOnlySequence<byte>?[] _fields = new ReadOnlySequence<byte>?[1];
    private BackendMessage _copyResponse;
    private ReadOnlySequence<byte> _exportInput;
    private NpgsqlCopyHarness _native = null!;
    private MemoryStream _output = null!;
    private long[] _values = [], _array = [];
    [Params(64, 4096)]
    public int Rows { get; set; }
    // 0 means scalar bigint; 256 means one bigint[] field per row.
    [Params(0, 256)]
    public int ArrayLength { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _values =
        [
            .. Enumerable.Range(0,
                Rows).Select(i => (long)i - Rows / 2)
        ];
        _array =
        [
            .. Enumerable.Range(0,
                ArrayLength).Select(i => (long)i - ArrayLength / 2)
        ];
        var payloadSize = checked(21 + Rows * (ArrayLength == 0 ? 14 : 26 + 12 * ArrayLength));
        var capacity = checked(payloadSize + (payloadSize / 4096 + 1) * 5);
        _native = new NpgsqlCopyHarness(capacity);
        _output = new MemoryStream(capacity);
        NpgsqlWrite();
        var expected = _native.Payload();
        MpgsqlWrite();
        var encoded = Unframe(_output.ToArray());
        if (!expected.AsSpan().SequenceEqual(encoded))
        {
            throw new InvalidOperationException("COPY payload differs from the Npgsql importer.");
        }
        MpgsqlBatchWrite();
        if (!expected.AsSpan().SequenceEqual(Unframe(_output.ToArray())))
        {
            throw new InvalidOperationException("COPY bigint batch differs from Npgsql.");
        }
        _exportInput = new ReadOnlySequence<byte>(_native.PrepareRead(encoded,
            Rows));
        var response = new ReadOnlySequence<byte>([(byte)'H', 0, 0, 0, 9, 1, 0, 1, 0, 1]);
        if (!BackendMessageReader.TryRead(ref response,
                out _copyResponse))
        {
            throw new InvalidDataException();
        }
        var sum = ArrayLength == 0 ? _values.Sum() : (long)Rows * ArrayLength;
        if (NpgsqlRead() != sum || MpgsqlRead() != sum)
        {
            throw new InvalidOperationException("COPY results differ from the Npgsql exporter.");
        }
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Write")]
    public int NpgsqlWrite()
    {
        return _native.Write(_values,
            _array,
            Rows,
            ArrayLength != 0);
    }
    [Benchmark, BenchmarkCategory("Write")]
    public int MpgsqlWrite()
    {
        return Write(false);
    }
    [Benchmark, BenchmarkCategory("Write")]
    public int MpgsqlBatchWrite()
    {
        return Write(true);
    }

    private int Write(bool batch)
    {
        _output.Position = 0;
        _output.SetLength(0);
        using var frames = new CopyDataWriter(_output);
        var copy = new BinaryCopyWriter(frames,
            1);
        if (batch && ArrayLength == 0)
        {
            for (var offset = 0; offset < Rows; offset += 512)
            {
                copy.WriteInt64Rows(_values.AsSpan(offset,
                    Math.Min(512,
                        Rows - offset)));
            }
        }
        else
        {
            for (var i = 0; i < Rows; i++)
            {
                copy.StartRow();
                if (ArrayLength == 0)
                {
                    copy.WriteInt64(_values[i]);
                }
                else
                {
                    copy.WriteLongArray(_array);
                }
            }
        }
        copy.Complete();
        frames.Flush();
        return (int)_output.Length;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Read")]
    public long NpgsqlRead()
    {
        return _native.Read(ArrayLength != 0);
    }
    [Benchmark, BenchmarkCategory("Read")]
    public long MpgsqlRead()
    {
        var copy = new BinaryCopyReader(1);
        var operation = new BinaryCopyOperation(_copyResponse);
        var input = _exportInput;
        long sum = 0;
        while (!input.IsEmpty)
        {
            if (!BackendMessageReader.TryRead(ref input,
                    out var message))
            {
                throw new InvalidDataException();
            }
            operation.Accept(message);
            if (message.Kind != BackendMessageKind.CopyData)
            {
                continue;
            }
            var body = message.GetCopyData();
            if (!copy.HeaderRead && !copy.TryReadHeader(ref body))
            {
                throw new InvalidDataException();
            }
            while (!body.IsEmpty)
            {
                var status = copy.TryReadRow(ref body,
                    _fields,
                    out var row);
                if (status == BinaryCopyReadStatus.NeedMoreData)
                {
                    throw new InvalidDataException();
                }
                if (status == BinaryCopyReadStatus.Row)
                {
                    sum = ArrayLength == 0 ? unchecked(sum + row.ReadInt64(0)) : sum + row.ReadLongArray(0).Length;
                }
            }
        }
        copy.EndData();
        if (!operation.IsCompleted || operation.RowsCopied != (ulong)Rows)
        {
            throw new InvalidDataException();
        }
        return sum;
    }

    private static byte[] Unframe(byte[] bytes)
    {
        var input = new ReadOnlySequence<byte>(bytes);
        var body = new ArrayBufferWriter<byte>();
        while (!input.IsEmpty)
        {
            if (!BackendMessageReader.TryRead(ref input,
                    out var message))
            {
                throw new InvalidDataException();
            }
            foreach (var memory in message.GetCopyData()) body.Write(memory.Span);
        }
        return [.. body.WrittenSpan];
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _native?.Dispose();
        _output?.Dispose();
    }

    internal static void Verify()
    {
        foreach (var rows in new[] {0, 1, 64, 4096})
        foreach (var length in new[] {0, 256})
        {
            var benchmark = new BinaryCopyBenchmarks {Rows = rows, ArrayLength = length};
            try { benchmark.Setup(); }
            finally { benchmark.Cleanup(); }
        }
        Console.WriteLine("Binary COPY rows/framing/results match the actual Npgsql 10.0.3 importer/exporter (memory transport).");
    }
}