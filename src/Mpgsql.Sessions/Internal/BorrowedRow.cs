using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Mpgsql.Protocol;

namespace Mpgsql.Internal;

// One reusable field index for an exclusive pull reader. The caller retains the
// input ReadResult or frame owner until movement; this type never copies or owns it.
internal sealed class BorrowedRow
{
    private int _count;
    private bool _initialized;
    private ReadOnlySequence<byte> _payload;
    private ReadOnlyMemory<byte> _contiguousPayload;
    private FieldIndex _single;
    private InlineFields _inline;
    private FieldIndex[]? _large;

    internal int Count => _count;
    internal bool IsInitialized => _initialized;

    internal void Initialize(BackendMessage message)
    {
        if (_initialized)
            Reset();
        if (message.Kind != BackendMessageKind.DataRow)
        {
            throw new InvalidOperationException("Expected a DataRow message.");
        }
        var payload = message.Payload;
        if (payload.Length < 2 || payload.Length > int.MaxValue)
        {
            throw new InvalidDataException("Invalid DataRow payload length.");
        }
        var expectedCount = message.GetDataRow().Count;
        if (payload.IsSingleSegment)
        {
            InitializeContiguous(payload.First, expectedCount);
            return;
        }
        var count = IndexFragmented(payload, expectedCount);
        _payload = payload;
        _count = count;
        _initialized = true;
    }

    internal void Initialize(ReadOnlyMemory<byte> contiguousPayload, int expectedCount)
    {
        if (_initialized)
            Reset();
        if (contiguousPayload.Length < 2)
        {
            throw new InvalidDataException("Invalid DataRow payload length.");
        }
        InitializeContiguous(contiguousPayload, expectedCount);
    }

    private void InitializeContiguous(ReadOnlyMemory<byte> payload, int expectedCount)
    {
        var count = IndexContiguous(payload.Span, expectedCount);
        _contiguousPayload = payload;
        _count = count;
        _initialized = true;
    }

    private int IndexContiguous(ReadOnlySpan<byte> bytes, int expectedCount)
    {
        var count = BinaryPrimitives.ReadUInt16BigEndian(bytes);
        if (count != expectedCount || count > (bytes.Length - 2) / 4)
        {
            throw new InvalidDataException("Invalid DataRow column count.");
        }
        var fields = GetIndexStorage(count);
        var offset = 2;
        for (var i = 0; i < count; i++)
        {
            if (bytes.Length - offset < 4)
            {
                throw new InvalidDataException("Truncated DataRow field length.");
            }
            var length = BinaryPrimitives.ReadInt32BigEndian(bytes[offset..]);
            offset += 4;
            if (length < -1 || length > bytes.Length - offset)
            {
                throw new InvalidDataException("Invalid DataRow field length.");
            }
            fields[i] = new FieldIndex(offset, length);
            if (length >= 0)
            {
                offset += length;
            }
        }
        if (offset != bytes.Length)
        {
            throw new InvalidDataException("Unexpected trailing DataRow bytes.");
        }
        return count;
    }

    private int IndexFragmented(ReadOnlySequence<byte> payload, int expectedCount)
    {
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryReadBigEndian(out short countField))
        {
            throw new InvalidDataException("Truncated DataRow column count.");
        }
        var count = unchecked((ushort)countField);
        if (count != expectedCount || count > reader.Remaining / 4)
        {
            throw new InvalidDataException("Invalid DataRow column count.");
        }
        var fields = GetIndexStorage(count);
        for (var i = 0; i < count; i++)
        {
            if (!reader.TryReadBigEndian(out int length))
            {
                throw new InvalidDataException("Truncated DataRow field length.");
            }
            if (length < -1 || length > reader.Remaining)
            {
                throw new InvalidDataException("Invalid DataRow field length.");
            }
            fields[i] = new FieldIndex((int)reader.Consumed, length);
            if (length >= 0)
            {
                reader.Advance(length);
            }
        }
        if (reader.Remaining != 0)
        {
            throw new InvalidDataException("Unexpected trailing DataRow bytes.");
        }
        return count;
    }

    private Span<FieldIndex> GetIndexStorage(int count)
    {
        if (count > 8)
        {
            if (_large is null || _large.Length < count)
            {
                _large = new FieldIndex[count];
            }
            return _large.AsSpan(0, count);
        }
        return count switch
        {
            0 => Span<FieldIndex>.Empty,
            1 => MemoryMarshal.CreateSpan(ref _single, 1),
            _ => _inline[..count]
        };
    }

    internal ReadOnlySequence<byte>? GetValue(int ordinal)
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("ReadAsync must position the reader on a row.");
        }
        if ((uint)ordinal >= (uint)_count)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }
        var field = _count switch
        {
            1 => _single,
            <= 8 => _inline[ordinal],
            _ => _large![ordinal]
        };
        if (field.Length == -1)
        {
            return null;
        }
        return _contiguousPayload.IsEmpty
            ? _payload.Slice(field.Offset, field.Length)
            : new ReadOnlySequence<byte>(_contiguousPayload.Slice(field.Offset, field.Length));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetContiguousValue(int ordinal, out ReadOnlySpan<byte> value, out bool isNull)
    {
        value = default;
        isNull = false;
        if (!_initialized)
        {
            throw new InvalidOperationException("ReadAsync must position the reader on a row.");
        }
        if ((uint)ordinal >= (uint)_count)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }
        if (_contiguousPayload.IsEmpty)
        {
            return false;
        }
        var field = _count switch
        {
            1 => _single,
            <= 8 => _inline[ordinal],
            _ => _large![ordinal]
        };
        isNull = field.Length == -1;
        if (!isNull)
        {
            value = _contiguousPayload.Span.Slice(field.Offset, field.Length);
        }
        return true;
    }

    internal void Reset()
    {
        _initialized = false;
        _payload = default;
        _contiguousPayload = default;
        _count = 0;
    }

    private readonly struct FieldIndex(int offset, int length)
    {
        internal readonly int Offset = offset;
        internal readonly int Length = length;
    }

    [InlineArray(8)]
    private struct InlineFields
    {
        private FieldIndex _element0;
    }
}
