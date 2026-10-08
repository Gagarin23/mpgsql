using System.Buffers;

namespace Mpgsql.Benchmarks.NpgsqlBaseline;

// Shared by both implementations: one preallocated, array-backed output buffer.
internal sealed class FixedBufferWriter(int capacity) : IBufferWriter<byte>
{
    private readonly byte[] _buffer = new byte[capacity];
    public int WrittenCount { get; private set; }
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan
    (
        0,
        WrittenCount
    );

    public void Advance(int count)
    {
        if ((uint)count > (uint)(_buffer.Length - WrittenCount))
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }
        WrittenCount += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        if ((uint)sizeHint > (uint)(_buffer.Length - WrittenCount))
        {
            throw new ArgumentOutOfRangeException(nameof(sizeHint));
        }
        return _buffer.AsMemory(WrittenCount);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        return GetMemory(sizeHint)
            .Span;
    }

    public void Reset()
    {
        WrittenCount = 0;
    }
}