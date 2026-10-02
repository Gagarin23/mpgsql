using System.Buffers;
using System.Buffers.Binary;
using BenchmarkDotNet.Attributes;
using Mpgsql.Internal;

namespace Mpgsql.Benchmarks;

// Isolates framing and row ownership; excludes session queues and cancellation dispatch.
[MemoryDiagnoser]
public class ResultBufferBenchmarks
{
    [Params(1, 128)]
    public int Rows { get; set; }
    [Params(8, 98324)]
    public int PayloadBytes { get; set; }
    [Params(0, 4096)]
    public int FragmentSize { get; set; }
    private BackendFrameBuffer _frames = null!;
    private byte[] _row = [];

    [GlobalSetup]
    public void Setup()
    {
        _frames = new();
        _row = new byte[11 + PayloadBytes];
        _row[0] = (byte)'D';
        BinaryPrimitives.WriteInt32BigEndian(_row.AsSpan(1),
            _row.Length - 1);
        BinaryPrimitives.WriteUInt16BigEndian(_row.AsSpan(5),
            1);
        BinaryPrimitives.WriteInt32BigEndian(_row.AsSpan(7),
            PayloadBytes);
        if (Buffered() != Rows || Discarded() != Rows)
        {
            throw new InvalidOperationException("Incomplete row framing.");
        }
    }

    [Benchmark(Baseline = true)]
    public int Buffered() => Read(discard: false);
    [Benchmark]
    public int Discarded() => Read(discard: true);

    private int Read(bool discard)
    {
        int completed = 0, fragment = FragmentSize == 0 ? _row.Length : FragmentSize;
        for (int i = 0; i < Rows; i++)
        for (int offset = 0; offset < _row.Length; offset += fragment)
        {
            var input = new ReadOnlySequence<byte>(_row.AsMemory(offset,
                Math.Min(fragment,
                    _row.Length - offset)));
            if (!_frames.TryRead(ref input,
                    discard,
                    out var message,
                    out var owner,
                    out _))
            {
                continue;
            }
            if (discard)
            {
                owner?.Dispose();
            }
            else
            {
                using var row = new OwnedRow(message,
                    owner);
                if (row[0]!.Value.Length != PayloadBytes)
                {
                    throw new InvalidDataException();
                }
            }
            completed++;
        }
        return completed;
    }

    [GlobalCleanup]
    public void Cleanup() => _frames.Dispose();
}