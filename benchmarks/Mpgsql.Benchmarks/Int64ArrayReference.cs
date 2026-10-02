using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Mpgsql.Converters;

namespace Mpgsql.Benchmarks;

// Deliberately straightforward, fully checked alternatives; not production converters.
internal static class Int64ArrayReference
{
    internal static int WriteScalar(ReadOnlyMemory<long> value,
        Span<byte> destination)
    {
        int size = Header(value,
            destination);
        int offset = 20;
        foreach (long item in value.Span)
        {
            BinaryPrimitives.WriteInt32BigEndian(destination[offset..],
                8);
            BinaryPrimitives.WriteInt64BigEndian(destination[(offset + 4)..],
                item);
            offset += 12;
        }
        return size;
    }

    internal static int WritePacked(ReadOnlyMemory<long> value,
        Span<byte> destination,
        bool fill)
    {
        int size = Header(value,
            destination);
        if (value.IsEmpty)
        {
            return size;
        }
        // Best case for the two-pass idea: pack into final storage, without a temporary buffer/copy.
        Pack(value.Span,
            MemoryMarshal.Cast<byte, int>(destination.Slice(20,
                size - 20)),
            fill);
        return size;
    }

    internal static int WritePooled(ReadOnlyMemory<long> value,
        Span<byte> destination,
        bool fill)
    {
        int size = Header(value,
            destination);
        if (value.IsEmpty)
        {
            return size;
        }
        int wordCount = checked(value.Length * 3);
        using var owner = MemoryPool<int>.Shared.Rent(wordCount);
        var words = owner.Memory.Span[..wordCount];
        Pack(value.Span,
            words,
            fill);
        MemoryMarshal.AsBytes(words).CopyTo(destination[20..]);
        return size;
    }

    private static void Pack(ReadOnlySpan<long> value,
        Span<int> words,
        bool fill)
    {
        if (fill)
        {
            words.Fill(8);
        }
        for (int i = 0; i < value.Length; i++)
        {
            long item = value[i];
            int offset = i * 3;
            if (!fill)
            {
                words[offset] = 8;
            }
            words[offset + 1] = (int)(item >> 32);
            words[offset + 2] = unchecked((int)item);
        }
        if (BitConverter.IsLittleEndian)
        {
            BinaryPrimitives.ReverseEndianness(words,
                words);
        }
    }

    private static int Header(ReadOnlyMemory<long> value,
        Span<byte> destination)
    {
        int size = Int64ArrayConverter.GetByteCount(value);
        if (destination.Length < size)
        {
            throw new ArgumentException("Insufficient capacity.");
        }
        if (MemoryMarshal.AsBytes(value.Span).Overlaps(destination[..size]))
        {
            throw new ArgumentException("Overlapping storage.");
        }
        BinaryPrimitives.WriteInt32BigEndian(destination,
            value.IsEmpty
                ? 0
                : 1);
        BinaryPrimitives.WriteInt32BigEndian(destination[4..],
            0);
        BinaryPrimitives.WriteUInt32BigEndian(destination[8..],
            20);
        if (!value.IsEmpty)
        {
            BinaryPrimitives.WriteInt32BigEndian(destination[12..],
                value.Length);
            BinaryPrimitives.WriteInt32BigEndian(destination[16..],
                1);
        }
        return size;
    }

    internal static int ReadScalar(ReadOnlySequence<byte> payload,
        Span<long> destination)
    {
        if (payload.IsSingleSegment)
        {
            return ReadScalar(payload.FirstSpan,
                destination);
        }
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryReadBigEndian(out int dimensions) || !reader.TryReadBigEndian(out int flags) ||
            !reader.TryReadBigEndian(out int oid))
        {
            throw new InvalidDataException();
        }
        int count = 0;
        int lowerBound = 0;
        if (dimensions == 1 && (!reader.TryReadBigEndian(out count) || !reader.TryReadBigEndian(out lowerBound)))
        {
            throw new InvalidDataException();
        }
        ValidateHeader(dimensions,
            flags,
            oid,
            count,
            lowerBound,
            payload.Length,
            destination.Length);
        foreach (var segment in payload)
            if (segment.Span.Overlaps(MemoryMarshal.AsBytes(destination[..count])))
            {
                throw new ArgumentException("Overlapping storage.");
            }
        for (int i = 0; i < count; i++)
        {
            if (!reader.TryReadBigEndian(out int length) || length != 8 || !reader.TryReadBigEndian(out long value))
            {
                throw new InvalidDataException();
            }
            destination[i] = value;
        }
        return count;
    }

    private static int ReadScalar(ReadOnlySpan<byte> payload,
        Span<long> destination)
    {
        if (payload.Length < 12)
        {
            throw new InvalidDataException();
        }
        int dimensions = BinaryPrimitives.ReadInt32BigEndian(payload);
        int flags = BinaryPrimitives.ReadInt32BigEndian(payload[4..]);
        int oid = BinaryPrimitives.ReadInt32BigEndian(payload[8..]);
        if (dimensions == 1 && payload.Length < 20)
        {
            throw new InvalidDataException();
        }
        int count = dimensions == 1 ? BinaryPrimitives.ReadInt32BigEndian(payload[12..]) : 0;
        int lowerBound = dimensions == 1 ? BinaryPrimitives.ReadInt32BigEndian(payload[16..]) : 0;
        ValidateHeader(dimensions,
            flags,
            oid,
            count,
            lowerBound,
            payload.Length,
            destination.Length);
        if (payload.Overlaps(MemoryMarshal.AsBytes(destination[..count])))
        {
            throw new ArgumentException("Overlapping storage.");
        }
        for (int i = 0; i < count; i++)
        {
            if (BinaryPrimitives.ReadInt32BigEndian(payload[(20 + i * 12)..]) != 8)
            {
                throw new InvalidDataException();
            }
            destination[i] = BinaryPrimitives.ReadInt64BigEndian(payload[(24 + i * 12)..]);
        }
        return count;
    }

    private static void ValidateHeader(int dimensions,
        int flags,
        int oid,
        int count,
        int lowerBound,
        long size,
        int capacity)
    {
        if ((uint)dimensions > 6 || (uint)flags > 1 || oid != 20 || count < 0 || (long)lowerBound + count > int.MaxValue)
        {
            throw new InvalidDataException();
        }
        if (dimensions > 1 || flags != 0)
        {
            throw new NotSupportedException();
        }
        if (size != (dimensions == 0 ? 12 : 20) + (long)count * 12)
        {
            throw new InvalidDataException();
        }
        if (capacity < count)
        {
            throw new ArgumentException("Insufficient capacity.");
        }
    }
}