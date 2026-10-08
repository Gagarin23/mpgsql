using System.Buffers;
using System.Buffers.Binary;
using BenchmarkDotNet.Attributes;
using Mpgsql.Benchmarks.Queries;
using Mpgsql.Protocol;

namespace Mpgsql.Benchmarks;

// Framing diagnostic only: Session defers row indexing to the ownership layer, excluded here.
[MemoryDiagnoser, JsonExporterAttribute.Full]
[Config(typeof(QueryBenchmarkConfig)), IterationTime(150)]
public class BackendFrameBenchmarks
{
    [Params("Control", "OneRow", "WideRows")] public string Transcript { get; set; } = "Control";
    [Params("Array", "Linked", "Pipe4KiB", "SplitHeaders")] public string Layout { get; set; } = "Array";
    private ReadOnlySequence<byte> _input;

    [GlobalSetup]
    public void Setup()
    {
        byte[] bytes = Transcript switch
        {
            "Control" => [.. QueryWire.Frame('1', []), .. QueryWire.Frame('2', []), .. QueryWire.Ready],
            "OneRow" => [.. QueryWire.Reply(QueryScenario.One, 0), .. QueryWire.Ready],
            "WideRows" => [.. QueryWire.Reply(QueryScenario.Wide, 0), .. QueryWire.Ready],
            _ => throw new ArgumentOutOfRangeException(nameof(Transcript))
        };
        _input = Layout switch
        {
            "Array" => new(bytes),
            "Linked" => Linked(bytes, bytes.Length),
            "Pipe4KiB" => Linked(bytes, 4096),
            "SplitHeaders" => Split(bytes),
            _ => throw new ArgumentOutOfRangeException(nameof(Layout))
        };
        long expected = 0;
        int offset = 0;
        while (offset < bytes.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset + 1));
            expected += bytes[offset] + length - 4;
            offset += length + 1;
        }
        if (PublicFrames() != expected || SessionFrames() != expected)
            throw new InvalidOperationException("Backend framing checksum mismatch.");
    }

    [Benchmark]
    public long PublicFrames()
    {
        var input = _input;
        long checksum = 0;
        while (BackendMessageReader.TryRead(ref input, out var message))
            checksum += (int)message.Kind + message.Payload.Length;
        return checksum + input.Length;
    }

    [Benchmark]
    public long SessionFrames()
    {
        var input = _input;
        long checksum = 0;
        while (BackendMessageReader.TryReadForSession(ref input, out var message))
            checksum += (int)message.Kind + message.Payload.Length;
        return checksum + input.Length;
    }

    private static ReadOnlySequence<byte> Linked(byte[] bytes, int chunk)
    {
        var first = new Segment(bytes.AsMemory(0, Math.Min(chunk, bytes.Length)));
        var last = first;
        for (int offset = first.Memory.Length; offset < bytes.Length; offset += chunk)
            last = last.Append(bytes.AsMemory(offset, Math.Min(chunk, bytes.Length - offset)));
        return new(first, 0, last, last.Memory.Length);
    }

    private static ReadOnlySequence<byte> Split(byte[] bytes)
    {
        var first = new Segment(ReadOnlyMemory<byte>.Empty);
        var last = first;
        for (int offset = 0; offset < bytes.Length;)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset + 1));
            // Every header crosses a segment boundary; payload remains contiguous.
            last = last.Append(bytes.AsMemory(offset, 2));
            last = last.Append(bytes.AsMemory(offset + 2, length - 1));
            offset += length + 1;
        }
        return new(first, 0, last, last.Memory.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        internal Segment(ReadOnlyMemory<byte> memory) => Memory = memory;
        internal Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}
