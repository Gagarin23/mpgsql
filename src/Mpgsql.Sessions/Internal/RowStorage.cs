using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Mpgsql.Protocol;

namespace Mpgsql.Internal;

// Only offsets are retained per field: row storage is contiguous after copying or frame assembly.
// An inactive storage can retain its own buffer of at most 128 bytes, but never the original caller buffer,
// frame owner, field memory or row-budget reservation. Borrowed field lifetime is unchanged.
internal sealed class RowStorage(RowStoragePool? pool)
{
    private const int SmallBufferCapacity = 128;
    internal RowStorage? Next;
    private RowBufferBudget? _budget;
    private byte[]? _buffer;
    private long _bytes;
    private int _count;
    private long _generation;
    private int[]? _offsets;
    private IMemoryOwner<byte>? _owner;
    private ReadOnlyMemory<byte> _payload;
    private byte[]? _smallBuffer;
    private int _singleOffset;
    private InlineOffsets _inlineOffsets;

    [InlineArray(8)]
    private struct InlineOffsets
    {
        private int _element0;
    }

    internal bool CanReuse => Volatile.Read(ref _generation) < long.MaxValue - 1;

    internal long BeginLease()
    {
        // A new storage or a pool pop belongs exclusively to this renter. Release publishes
        // the next even generation before cleanup and Return; stale handles can only fail
        // their CAS while this renter publishes the next odd generation. CanReuse retires
        // storage before the counter can wrap, so an earlier odd generation cannot alias it.
        var generation = _generation + 1;
        Volatile.Write(ref _generation, generation);
        return generation;
    }

    internal void Initialize(
        BackendMessage message, IMemoryOwner<byte>? owner,
        RowBufferBudget? budget
    )
    {
        _count = message.GetDataRow()
            .Count;
        _bytes = message.Payload.Length;
        if (owner is not null && message.Payload.IsSingleSegment)
        {
            _payload = message.Payload.First;
        }
        else if (_bytes <= SmallBufferCapacity)
        {
            var buffer = _smallBuffer;
            if (buffer is null || buffer.Length < _bytes)
            {
                // Retain the smallest fitting tier, then reuse its high-water capacity.
                var capacity = _bytes switch
                {
                    <= 16 => 16,
                    <= 32 => 32,
                    <= 64 => 64,
                    _ => SmallBufferCapacity
                };
                _smallBuffer = buffer = new byte[capacity];
            }
            message.Payload.CopyTo(buffer);
            _payload = buffer.AsMemory(0, (int)_bytes);
        }
        else
        {
            _buffer = ArrayPool<byte>.Shared.Rent(checked((int)_bytes));
            message.Payload.CopyTo(_buffer);
            _payload = _buffer.AsMemory(0, (int)_bytes);
        }
        // Keep scalar access separate and small row offsets inside the pooled storage.
        var offsets = _count switch
        {
            0 => Span<int>.Empty,
            1 => MemoryMarshal.CreateSpan(ref _singleOffset, 1),
            <= 8 => _inlineOffsets[.._count],
            _ => (_offsets = ArrayPool<int>.Shared.Rent(_count)).AsSpan(0, _count)
        };
        var bytes = _payload.Span;
        var offset = 2;
        for (var i = 0;
             i < _count;
             i++)
        {
            if (bytes.Length - offset < 4)
            {
                throw new InvalidDataException("Truncated DataRow field length.");
            }
            var length = BinaryPrimitives.ReadInt32BigEndian(bytes[offset..]);
            if (length == -1)
            {
                offsets[i] = -1;
                offset += 4;
            }
            else
            {
                if (length < 0 || length > bytes.Length - offset - 4)
                {
                    throw new InvalidDataException("Invalid DataRow field length.");
                }
                offsets[i] = offset;
                offset += 4 + length;
            }
        }
        if (offset != bytes.Length)
        {
            throw new InvalidDataException("Unexpected trailing DataRow bytes.");
        }
        // The caller keeps both ownerships on an initialization failure.
        _owner = owner;
        _budget = budget;
    }

    internal ReadOnlySequence<byte>? GetValue(long generation, int ordinal)
    {
        if (Volatile.Read(ref _generation) != generation)
        {
            throw new ObjectDisposedException(nameof(OwnedRow));
        }
        if ((uint)ordinal >= (uint)_count)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }
        var offset = _count switch
        {
            1 => _singleOffset,
            <= 8 => _inlineOffsets[ordinal],
            _ => _offsets![ordinal]
        };
        return offset == -1
            ? null
            : new ReadOnlySequence<byte>
            (
                _payload.Slice
                (
                    offset + 4,
                    BinaryPrimitives.ReadInt32BigEndian(_payload.Span[offset..])
                )
            );
    }

    internal void Release(long generation)
    {
        if (Interlocked.CompareExchange(ref _generation, generation + 1, generation) != generation)
        {
            return;
        }
        if (_offsets is { } offsets)
        {
            _offsets = null;
            ArrayPool<int>.Shared.Return(offsets);
        }
        _payload = default;
        var owner = _owner;
        _owner = null;
        try { owner?.Dispose(); }
        finally
        {
            if (_buffer is { } buffer)
            {
                _buffer = null;
                ArrayPool<byte>.Shared.Return(buffer);
            }
            var budget = _budget;
            _budget = null;
            budget?.Release(_bytes);
            _count = 0;
            _bytes = 0;
            pool?.Return(this);
        }
    }
}
