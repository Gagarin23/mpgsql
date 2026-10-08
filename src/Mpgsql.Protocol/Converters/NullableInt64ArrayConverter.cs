using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL bigint[] conversion for ReadOnlyMemory&lt;long?&gt;.</summary>
/// <remarks>
///     Encodes the array payload only; Bind/DataRow own the outer value length.
///     All wire integers are big-endian. A NULL element is just an Int32 -1 length;
///     a non-NULL element is an Int32 8 length followed by its Int64 value.
///     Empty memory is an empty array, never SQL NULL. Only zero/one-dimensional arrays
///     are supported. Reading normalizes PostgreSQL lower bounds to zero-based indexing.
/// </remarks>
public static class NullableInt64ArrayConverter
{
    public const uint ElementTypeOid = (uint)TypeOid.Int64;
    public const uint ArrayTypeOid = (uint)TypeOid.Int64Array;

    private const int EmptyHeaderSize = 12;
    private const int HeaderSize = 20;
    private const int RecordSize = 4 + sizeof(long);

    public static int GetByteCount(ReadOnlyMemory<long?> value)
    {
        return Measure
        (
            value.Span,
            out _
        );
    }

    public static int GetByteCount(
        int elementCount,
        int nullCount
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elementCount);
        ArgumentOutOfRangeException.ThrowIfNegative(nullCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan
        (
            nullCount,
            elementCount
        );
        return elementCount == 0
            ? EmptyHeaderSize
            : checked(HeaderSize + 4 * elementCount + sizeof(long) * (elementCount - nullCount));
    }

    /// <summary>Writes directly into the destination, returning the exact payload size.</summary>
    /// <remarks>Checks capacity and rejects overlapping storage before changing the destination.</remarks>
    public static int Write(
        ReadOnlyMemory<long?> value,
        Span<byte> destination
    )
    {
        var source = value.Span;
        var size = Measure
        (
            source,
            out var nullCount
        );
        if (destination.Length < size)
        {
            throw new ArgumentException
            (
                "The destination is too small for the bigint[] payload.",
                nameof(destination)
            );
        }
        destination = destination[..size];
        RequireSeparateStorage
        (
            AsBytes(source),
            destination
        );
        WriteCore
        (
            source,
            destination,
            nullCount
        );
        return size;
    }

    /// <summary>Reserves the exact payload and advances only the written region.</summary>
    public static void Write(
        ReadOnlyMemory<long?> value,
        IBufferWriter<byte> destination
    )
    {
        WriteBuffered
        (
            value,
            destination,
            false
        );
    }

    // COPY uses the same measurement pass and output reservation as a plain array write.
    internal static void WriteCopyField(
        ReadOnlyMemory<long?> value,
        IBufferWriter<byte> destination
    )
    {
        WriteBuffered
        (
            value,
            destination,
            true
        );
    }

    private static void WriteBuffered(
        ReadOnlyMemory<long?> value,
        IBufferWriter<byte> destination,
        bool lengthPrefix
    )
    {
        ArgumentNullException.ThrowIfNull(destination);
        var source = value.Span;
        var payloadSize = Measure
        (
            source,
            out var nullCount
        );
        var prefixSize = lengthPrefix ? 4 : 0;
        var size = checked(prefixSize + payloadSize);
        var bytes = destination
            .GetSpan(size)[..size];
        RequireSeparateStorage
        (
            AsBytes(source),
            bytes
        );
        WriteCore
        (
            source,
            bytes[prefixSize..],
            nullCount
        );
        if (lengthPrefix)
        {
            BinaryPrimitives.WriteInt32BigEndian
            (
                bytes,
                payloadSize
            );
        }
        destination.Advance(size);
    }

    /// <summary>Returns independently owned memory with one long?[] allocation for a nonempty array.</summary>
    public static ReadOnlyMemory<long?> Read(ReadOnlySpan<byte> payload)
    {
        var count = ReadHeader
        (
            payload,
            out var headerSize
        );
        if (count == 0)
        {
            return ReadOnlyMemory<long?>.Empty;
        }
        var result = GC.AllocateUninitializedArray<long?>(count);
        ReadRecords
        (
            payload[headerSize..],
            result
        );
        return result;
    }

    /// <summary>Decodes into reusable storage without allocating; returns the element count.</summary>
    /// <remarks>
    ///     Capacity and overlap are checked before writing. On an invalid element prefix or
    ///     truncated element, part of the destination may have been written.
    /// </remarks>
    public static int Read(
        ReadOnlySpan<byte> payload,
        Span<long?> destination
    )
    {
        var count = ReadHeader
        (
            payload,
            out var headerSize
        );
        RequireCapacity
        (
            count,
            destination.Length
        );
        destination = destination[..count];
        RequireSeparateStorage
        (
            payload,
            AsBytes(destination)
        );
        ReadRecords
        (
            payload[headerSize..],
            destination
        );
        return count;
    }

    /// <summary>Reads segmented input without consolidating the payload bytes.</summary>
    public static ReadOnlyMemory<long?> Read(ReadOnlySequence<byte> payload)
    {
        if (payload.IsSingleSegment)
        {
            return Read(payload.FirstSpan);
        }
        var reader = new SequenceReader<byte>(payload);
        var count = ReadHeader(ref reader);
        if (count == 0)
        {
            return ReadOnlyMemory<long?>.Empty;
        }
        var result = GC.AllocateUninitializedArray<long?>(count);
        ReadSegmentedRecords
        (
            ref reader,
            result
        );
        return result;
    }

    /// <summary>Reads segmented input into reusable storage without copying or allocating.</summary>
    /// <remarks>On a malformed element, part of the destination may have been written.</remarks>
    public static int Read(
        ReadOnlySequence<byte> payload,
        Span<long?> destination
    )
    {
        if (payload.IsSingleSegment)
        {
            return Read
            (
                payload.FirstSpan,
                destination
            );
        }
        var reader = new SequenceReader<byte>(payload);
        var count = ReadHeader(ref reader);
        RequireCapacity
        (
            count,
            destination.Length
        );
        destination = destination[..count];
        var outputBytes = AsBytes(destination);
        foreach (var segment in payload)
        {
            RequireSeparateStorage
            (
                segment.Span,
                outputBytes
            );
        }
        ReadSegmentedRecords
        (
            ref reader,
            destination
        );
        return count;
    }

    private static int Measure(
        ReadOnlySpan<long?> source,
        out int nullCount
    )
    {
        nullCount = 0;
        foreach (var item in source)
        {
            nullCount += item.HasValue ? 0 : 1;
        }
        return GetByteCount
        (
            source.Length,
            nullCount
        );
    }

    private static void WriteCore(
        ReadOnlySpan<long?> source,
        Span<byte> destination,
        int nullCount
    )
    {
        // Int32 ndim, Int32 has-NULL flag, UInt32 element OID, then count/lower bound.
        BinaryPrimitives.WriteInt32BigEndian
        (
            destination,
            source.IsEmpty
                ? 0
                : 1
        );
        BinaryPrimitives.WriteInt32BigEndian
        (
            destination[4..],
            nullCount != 0
                ? 1
                : 0
        );
        BinaryPrimitives.WriteUInt32BigEndian
        (
            destination[8..],
            ElementTypeOid
        );
        if (source.IsEmpty)
        {
            return;
        }
        BinaryPrimitives.WriteInt32BigEndian
        (
            destination[12..],
            source.Length
        );
        BinaryPrimitives.WriteInt32BigEndian
        (
            destination[16..],
            1
        );
        if (nullCount == source.Length)
        {
            // All records are just -1 prefixes: FF is endian-independent, with no overwritten values.
            destination[HeaderSize..]
                .Fill(0xff);
            return;
        }
        var offset = HeaderSize;
        if (nullCount == 0)
        {
            foreach (var item in source)
            {
                BinaryPrimitives.WriteInt32BigEndian
                (
                    destination[offset..],
                    sizeof(long)
                );
                BinaryPrimitives.WriteInt64BigEndian
                (
                    destination[(offset + 4)..],
                    item.GetValueOrDefault()
                );
                offset += RecordSize;
            }
            return;
        }
        foreach (var item in source)
        {
            BinaryPrimitives.WriteInt32BigEndian
            (
                destination[offset..],
                item.HasValue
                    ? sizeof(long)
                    : -1
            );
            offset += 4;
            if (!item.HasValue)
            {
                continue;
            }
            BinaryPrimitives.WriteInt64BigEndian
            (
                destination[offset..],
                item.GetValueOrDefault()
            );
            offset += sizeof(long);
        }
    }

    private static int ReadHeader(
        ReadOnlySpan<byte> payload,
        out int headerSize
    )
    {
        if (payload.Length < EmptyHeaderSize)
        {
            throw new InvalidDataException("Truncated PostgreSQL array header.");
        }
        var dimensions = BinaryPrimitives.ReadInt32BigEndian(payload);
        ValidateArrayType
        (
            dimensions,
            BinaryPrimitives.ReadInt32BigEndian(payload[4..]),
            BinaryPrimitives.ReadUInt32BigEndian(payload[8..])
        );
        headerSize = dimensions == 0 ? EmptyHeaderSize : HeaderSize;
        if (payload.Length < headerSize)
        {
            throw new InvalidDataException("Truncated PostgreSQL array dimension.");
        }
        var count = dimensions == 0 ? 0 : BinaryPrimitives.ReadInt32BigEndian(payload[12..]);
        var lowerBound = dimensions == 0 ? 0 : BinaryPrimitives.ReadInt32BigEndian(payload[16..]);
        ValidateBoundsAndLength
        (
            count,
            lowerBound,
            headerSize,
            payload.Length
        );
        return count;
    }

    private static int ReadHeader(ref SequenceReader<byte> reader)
    {
        var size = reader.Remaining;
        if (!reader.TryReadBigEndian(out int dimensions) || !reader.TryReadBigEndian(out int flags) ||
            !reader.TryReadBigEndian(out int oid))
        {
            throw new InvalidDataException("Truncated PostgreSQL array header.");
        }
        ValidateArrayType
        (
            dimensions,
            flags,
            unchecked((uint)oid)
        );
        int count = 0,
            lowerBound = 0;
        if (dimensions == 1 && (!reader.TryReadBigEndian(out count) || !reader.TryReadBigEndian(out lowerBound)))
        {
            throw new InvalidDataException("Truncated PostgreSQL array dimension.");
        }
        ValidateBoundsAndLength
        (
            count,
            lowerBound,
            dimensions == 0
                ? EmptyHeaderSize
                : HeaderSize,
            size
        );
        return count;
    }

    private static void ValidateArrayType(
        int dimensions,
        int flags,
        uint oid
    )
    {
        if ((uint)dimensions > 6 || (uint)flags > 1 || oid != ElementTypeOid)
        {
            throw new InvalidDataException("Invalid bigint[] dimensions, flags, or element OID.");
        }
        if (dimensions > 1)
        {
            throw new NotSupportedException("ReadOnlyMemory<long?> cannot represent a multidimensional PostgreSQL array.");
        }
        // PostgreSQL array_recv checks flags, but actual NULLs are identified by element lengths.
    }

    private static void ValidateBoundsAndLength(
        int count,
        int lowerBound,
        int headerSize,
        long payloadLength
    )
    {
        if (count < 0 || (long)lowerBound + count > int.MaxValue)
        {
            throw new InvalidDataException("Invalid PostgreSQL array bounds.");
        }
        // A NULL occupies four bytes; a value occupies twelve. Bound allocations by actual input.
        if (payloadLength < headerSize + 4L * count || payloadLength > headerSize + (long)RecordSize * count)
        {
            throw new InvalidDataException("The bigint[] payload length does not match its element count.");
        }
    }

    private static void ReadRecords(
        ReadOnlySpan<byte> records,
        Span<long?> destination
    )
    {
        if (records.Length == 4L * destination.Length)
        {
            ValidateAllNullRecords(records);
            destination.Clear();
            return;
        }
        var count = ReadAvailableRecords
        (
            records,
            destination,
            out var consumed
        );
        if (count != destination.Length || consumed != records.Length)
        {
            throw new InvalidDataException("Truncated element or unexpected trailing bigint[] bytes.");
        }
    }

    // Decode whole variable-length records within each segment, falling back only at boundaries.
    private static int ReadAvailableRecords(
        ReadOnlySpan<byte> records,
        Span<long?> destination,
        out int consumed
    )
    {
        int written = 0,
            offset = 0;
        while (written < destination.Length && records.Length - offset >= 4)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(records[offset..]);
            if (length == -1)
            {
                destination[written++] = null; // Clear previous values when reusing storage.
                offset += 4;
                continue;
            }
            if (length != sizeof(long))
            {
                throw new InvalidDataException("A non-NULL bigint[] element must have length 8.");
            }
            if (records.Length - offset < RecordSize)
            {
                break;
            }
            destination[written++] = BinaryPrimitives.ReadInt64BigEndian(records[(offset + 4)..]);
            offset += RecordSize;
        }
        consumed = offset;
        return written;
    }

    private static void ReadSegmentedRecords(
        ref SequenceReader<byte> reader,
        Span<long?> destination
    )
    {
        if (reader.Remaining == 4L * destination.Length)
        {
            foreach (var segment in reader.Sequence.Slice(reader.Position))
            {
                ValidateAllNullRecords(segment.Span);
            }
            destination.Clear();
            reader.Advance(reader.Remaining);
            return;
        }
        var written = 0;
        while (written < destination.Length)
        {
            var count = ReadAvailableRecords
            (
                reader.UnreadSpan,
                destination[written..],
                out var consumed
            );
            if (count != 0)
            {
                reader.Advance(consumed);
                written += count;
                continue;
            }
            if (!reader.TryReadBigEndian(out int length))
            {
                throw new InvalidDataException("Truncated PostgreSQL array element.");
            }
            if (length == -1)
            {
                destination[written++] = null;
            }
            else
            {
                if (length != sizeof(long) || !reader.TryReadBigEndian(out long item))
                {
                    throw new InvalidDataException("Invalid or truncated PostgreSQL array element.");
                }
                destination[written++] = item;
            }
        }
        if (reader.Remaining != 0)
        {
            throw new InvalidDataException("Unexpected trailing bigint[] bytes.");
        }
    }

    private static void ValidateAllNullRecords(ReadOnlySpan<byte> records)
    {
        if (records.IndexOfAnyExcept((byte)0xff) >= 0)
        {
            throw new InvalidDataException("An array with only four bytes per element must contain only NULL lengths.");
        }
    }

    private static void RequireCapacity(
        int count,
        int capacity
    )
    {
        if (capacity < count)
        {
            throw new ArgumentException
            (
                "The destination is too small for the bigint[] elements.",
                "destination"
            );
        }
    }

    private static void RequireSeparateStorage(
        ReadOnlySpan<byte> source,
        Span<byte> destination
    )
    {
        if (source.Overlaps(destination))
        {
            throw new ArgumentException
            (
                "Array input and output storage must not overlap.",
                "destination"
            );
        }
    }

    // MemoryMarshal.AsBytes rejects Nullable<T>. These views are used only for overlap checks;
    // no assumption is made about Nullable<long>'s fields, padding or value offset.
    private static ReadOnlySpan<byte> AsBytes(ReadOnlySpan<long?> values)
    {
        return MemoryMarshal.CreateReadOnlySpan
        (
            ref Unsafe.As<long?, byte>(ref MemoryMarshal.GetReference(values)),
            checked(values.Length * Unsafe.SizeOf<long?>())
        );
    }

    private static Span<byte> AsBytes(Span<long?> values)
    {
        return MemoryMarshal.CreateSpan
        (
            ref Unsafe.As<long?, byte>(ref MemoryMarshal.GetReference(values)),
            checked(values.Length * Unsafe.SizeOf<long?>())
        );
    }
}