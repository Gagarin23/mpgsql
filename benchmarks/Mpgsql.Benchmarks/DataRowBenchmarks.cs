#if PROTOCOL_BASELINE
extern alias baseline;
using OriginalReader = baseline::Mpgsql.Protocol.BackendMessageReader;
#endif
using System.Buffers;
using System.Buffers.Binary;
using BenchmarkDotNet.Attributes;
using Mpgsql.Protocol;
namespace Mpgsql.Benchmarks;

[MemoryDiagnoser, SimpleJob(1, 3, 8), IterationTime(150)]
public class DataRowBenchmarks
{

    private ReadOnlySequence<byte> _packet;
    private ReadOnlySequence<byte>?[] _storage = [];
    [Params(1, 8, 64)]
    public int Columns { get; set; }

    [Params(false, true)]
    public bool Fragmented { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var output = new ArrayBufferWriter<byte>();
        output.Write
        (
            new byte[]
            {
                (byte)'D',
                0,
                0,
                0,
                0,
                0,
                (byte)Columns
            }
        );
        for (var i = 0;
             i < Columns;
             i++)
        {
            // Mix ordinary, NULL, and empty values; all data is allocated outside timing.
            var length = i % 5 == 4 ? -1 : i % 5 == 3 ? 0 : 8;
            var prefix = output.GetSpan(4);
            BinaryPrimitives.WriteInt32BigEndian
            (
                prefix,
                length
            );
            output.Advance(4);
            if (length > 0)
            {
                output.Write(new byte[length]);
            }
        }
        byte[] bytes = [.. output.WrittenSpan];
        BinaryPrimitives.WriteInt32BigEndian
        (
            bytes.AsSpan
            (
                1,
                4
            ),
            bytes.Length - 1
        );
        _packet = Fragmented
            ? Segment
            (
                bytes,
                7
            )
            : new ReadOnlySequence<byte>(bytes);
        _storage = new ReadOnlySequence<byte>?[Columns];

        var expected = CurrentUnindexed();
        if (CurrentIndexed() != expected)
        {
            throw new InvalidOperationException("Indexed row consumption differs.");
        }
#if PROTOCOL_BASELINE
        if (Original() != expected)
        {
            throw new InvalidOperationException("Row consumption differs from the baseline.");
        }
#endif
    }

#if PROTOCOL_BASELINE
    [Benchmark(Baseline = true)]
    public long Original()
    {
        var input = _packet;
        if (!OriginalReader.TryRead
            (
                ref input,
                out var message
            ))
        {
            throw new InvalidOperationException();
        }
        long sum = message.GetDataRow()
            .Count;
        foreach (var value in message.GetDataRow())
        {
            sum += value?.Length ?? -1;
        }
        return sum + input.Length;
    }
#endif

    [Benchmark]
    public long CurrentUnindexed()
    {
        var input = _packet;
        if (!BackendMessageReader.TryRead
            (
                ref input,
                out var message
            ))
        {
            throw new InvalidOperationException();
        }
        long sum = message.GetDataRow()
            .Count;
        foreach (var value in message.GetDataRow())
        {
            sum += value?.Length ?? -1;
        }
        return sum + input.Length;
    }

    [Benchmark]
    public long CurrentIndexed()
    {
        var input = _packet;
        if (!BackendMessageReader.TryRead
            (
                ref input,
                _storage,
                out _,
                out var row
            ))
        {
            throw new InvalidOperationException();
        }
        long sum = row.Count;
        foreach (var value in row)
        {
            sum += value?.Length ?? -1;
        }
        return sum + input.Length;
    }

    private static ReadOnlySequence<byte> Segment(
        byte[] bytes,
        int size
    )
    {
        var first = new SegmentNode
        (
            bytes.AsMemory
            (
                0,
                Math.Min
                (
                    size,
                    bytes.Length
                )
            )
        );
        var last = first;
        for (var start = size;
             start < bytes.Length;
             start += size)
        {
            last = last.Append
            (
                bytes.AsMemory
                (
                    start,
                    Math.Min
                    (
                        size,
                        bytes.Length - start
                    )
                )
            );
        }
        return new ReadOnlySequence<byte>
        (
            first,
            0,
            last,
            last.Memory.Length
        );
    }

    private sealed class SegmentNode : ReadOnlySequenceSegment<byte>
    {
        public SegmentNode(ReadOnlyMemory<byte> memory)
        {
            Memory = memory;
        }

        public SegmentNode Append(ReadOnlyMemory<byte> memory)
        {
            var next = new SegmentNode(memory)
            {
                RunningIndex = RunningIndex + Memory.Length
            };
            Next = next;
            return next;
        }
    }
}