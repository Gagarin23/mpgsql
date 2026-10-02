using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL bigint[] conversion for ReadOnlyMemory&lt;long&gt;.</summary>
/// <remarks>
/// Encodes the array payload only; Bind/DataRow own the outer value length.
/// All wire integers are big-endian. Empty memory is an empty array, never SQL NULL.
/// Only zero/one-dimensional arrays without NULL elements are supported.
/// Reading normalizes PostgreSQL lower bounds to the memory's zero-based indexing.
/// </remarks>
public static partial class Int64ArrayConverter
{
    public const uint ElementTypeOid = (uint)TypeOid.Int64;
    public const uint ArrayTypeOid = (uint)TypeOid.Int64Array;

    private const int EmptyHeaderSize = 12;
    private const int HeaderSize = 20;
    private const int RecordSize = 4 + sizeof(long);

    public static int GetByteCount(ReadOnlyMemory<long> value) => GetByteCount(value.Length);

    public static int GetByteCount(int elementCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elementCount);
        return elementCount == 0 ? EmptyHeaderSize : checked(HeaderSize + RecordSize * elementCount);
    }

    /// <summary>Writes directly into the destination, returning the payload size.</summary>
    /// <remarks>Checks capacity and rejects overlapping storage before changing the destination.</remarks>
    public static int Write(ReadOnlyMemory<long> value,
        Span<byte> destination)
    {
        int size = GetByteCount(value);
        if (destination.Length < size)
        {
            throw new ArgumentException("The destination is too small for the bigint[] payload.",
                nameof(destination));
        }
        var source = value.Span;
        destination = destination[..size];
        RequireSeparateStorage(MemoryMarshal.AsBytes(source),
            destination);
        WriteCore(source,
            destination);
        return size;
    }

    /// <summary>Reserves the complete payload and advances only the written region.</summary>
    public static void Write(ReadOnlyMemory<long> value,
        IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        int size = GetByteCount(value);
        Write(value,
            destination.GetSpan(size));
        destination.Advance(size);
    }

    /// <summary>Returns independently owned memory with one long[] allocation for a nonempty array.</summary>
    public static ReadOnlyMemory<long> Read(ReadOnlySpan<byte> payload)
    {
        int count = ReadHeader(payload,
            out int headerSize,
            out bool nullable);
        if (nullable)
        {
            ValidateNullableRecords(payload[headerSize..],
                count);
        }
        if (count == 0)
        {
            return ReadOnlyMemory<long>.Empty;
        }
        var result = GC.AllocateUninitializedArray<long>(count);
        ReadRecords(payload[headerSize..],
            result);
        return result;
    }

    /// <summary>Decodes into reusable storage without allocating; returns the element count.</summary>
    /// <remarks>On an invalid element prefix, part of the destination may have been written.</remarks>
    public static int Read(ReadOnlySpan<byte> payload,
        Span<long> destination)
    {
        int count = ReadHeader(payload,
            out int headerSize,
            out bool nullable);
        if (nullable)
        {
            ValidateNullableRecords(payload[headerSize..],
                count);
        }
        RequireCapacity(count,
            destination.Length);
        destination = destination[..count];
        RequireSeparateStorage(payload,
            MemoryMarshal.AsBytes(destination));
        ReadRecords(payload[headerSize..],
            destination);
        return count;
    }

    /// <summary>Reads a possibly segmented DataRow value without consolidating its bytes.</summary>
    public static ReadOnlyMemory<long> Read(ReadOnlySequence<byte> payload)
    {
        if (payload.IsSingleSegment)
        {
            return Read(payload.FirstSpan);
        }
        var reader = new SequenceReader<byte>(payload);
        int count = ReadHeader(ref reader,
            out bool nullable);
        if (nullable)
        {
            ValidateNullableRecords(reader,
                count);
        }
        if (count == 0)
        {
            return ReadOnlyMemory<long>.Empty;
        }
        var result = GC.AllocateUninitializedArray<long>(count);
        ReadSegmentedRecords(ref reader,
            result);
        return result;
    }

    /// <summary>Reads segmented input into reusable storage without a payload copy or allocation.</summary>
    /// <remarks>On an invalid element prefix, part of the destination may have been written.</remarks>
    public static int Read(ReadOnlySequence<byte> payload,
        Span<long> destination)
    {
        if (payload.IsSingleSegment)
        {
            return Read(payload.FirstSpan,
                destination);
        }
        var reader = new SequenceReader<byte>(payload);
        int count = ReadHeader(ref reader,
            out bool nullable);
        if (nullable)
        {
            ValidateNullableRecords(reader,
                count);
        }
        RequireCapacity(count,
            destination.Length);
        destination = destination[..count];
        var outputBytes = MemoryMarshal.AsBytes(destination);
        foreach (var segment in payload)
            RequireSeparateStorage(segment.Span,
                outputBytes);
        ReadSegmentedRecords(ref reader,
            destination);
        return count;
    }

    private static void WriteCore(ReadOnlySpan<long> source,
        Span<byte> destination)
    {
        // Int32 ndim, Int32 flags, UInt32 element OID, then Int32 count/lower bound per dimension.
        BinaryPrimitives.WriteInt32BigEndian(destination,
            source.IsEmpty
                ? 0
                : 1);
        BinaryPrimitives.WriteInt32BigEndian(destination[4..],
            0);
        BinaryPrimitives.WriteUInt32BigEndian(destination[8..],
            ElementTypeOid);
        if (source.IsEmpty)
        {
            return;
        }
        BinaryPrimitives.WriteInt32BigEndian(destination[12..],
            source.Length);
        BinaryPrimitives.WriteInt32BigEndian(destination[16..],
            1);
        WriteRecords(source,
            destination[HeaderSize..]);
    }

    private static int ReadHeader(ref SequenceReader<byte> reader,
        out bool nullable)
    {
        long payloadLength = reader.Remaining;
        if (!reader.TryReadBigEndian(out int dimensions) || !reader.TryReadBigEndian(out int flags) ||
            !reader.TryReadBigEndian(out int oid))
        {
            throw new InvalidDataException("Truncated PostgreSQL array header.");
        }
        nullable = ValidateArrayType(dimensions,
            flags,
            unchecked((uint)oid));
        int count = 0;
        int lowerBound = 0;
        if (dimensions == 1 && (!reader.TryReadBigEndian(out count) || !reader.TryReadBigEndian(out lowerBound)))
        {
            throw new InvalidDataException("Truncated PostgreSQL array dimension.");
        }
        ValidateBoundsAndLength(count,
            lowerBound,
            dimensions == 0
                ? EmptyHeaderSize
                : HeaderSize,
            payloadLength,
            nullable);
        return count;
    }

    private static int ReadHeader(ReadOnlySpan<byte> payload,
        out int headerSize,
        out bool nullable)
    {
        if (payload.Length < EmptyHeaderSize)
        {
            throw new InvalidDataException("Truncated PostgreSQL array header.");
        }
        int dimensions = BinaryPrimitives.ReadInt32BigEndian(payload);
        int flags = BinaryPrimitives.ReadInt32BigEndian(payload[4..]);
        uint oid = BinaryPrimitives.ReadUInt32BigEndian(payload[8..]);
        nullable = ValidateArrayType(dimensions,
            flags,
            oid);
        headerSize = dimensions == 0 ? EmptyHeaderSize : HeaderSize;
        if (payload.Length < headerSize)
        {
            throw new InvalidDataException("Truncated PostgreSQL array dimension.");
        }
        int count = dimensions == 0 ? 0 : BinaryPrimitives.ReadInt32BigEndian(payload[12..]);
        int lowerBound = dimensions == 0 ? 0 : BinaryPrimitives.ReadInt32BigEndian(payload[16..]);
        ValidateBoundsAndLength(count,
            lowerBound,
            headerSize,
            payload.Length,
            nullable);
        return count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ValidateArrayType(int dimensions,
        int flags,
        uint oid)
    {
        if ((uint)dimensions > 6 || (uint)flags > 1 || oid != ElementTypeOid)
        {
            throw new InvalidDataException("Invalid bigint[] dimensions, flags, or element OID.");
        }
        if (dimensions > 1)
        {
            throw new NotSupportedException("ReadOnlyMemory<long> cannot represent a multidimensional PostgreSQL array.");
        }
        return flags != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ValidateBoundsAndLength(int count,
        int lowerBound,
        int headerSize,
        long payloadLength,
        bool nullable)
    {
        // PostgreSQL requires lowerBound + count to fit Int32, including the exclusive upper bound.
        if (count < 0 || (long)lowerBound + count > int.MaxValue)
        {
            throw new InvalidDataException("Invalid PostgreSQL array bounds.");
        }
        // Validate exact framing before allocating from an untrusted element count.
        long expected = headerSize + (long)RecordSize * count;
        // A nullable record can be only its four-byte -1 length. The rare nullable path validates
        // every actual prefix before allocating/writing, accepting flags=1 without actual NULLs.
        if (nullable ? payloadLength < headerSize + 4L * count || payloadLength > expected : payloadLength != expected)
        {
            throw new InvalidDataException("The bigint[] payload length does not match its element count.");
        }
    }

    private static void ValidateNullableRecords(ReadOnlySpan<byte> records,
        int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (records.Length < 4)
            {
                throw new InvalidDataException("Truncated PostgreSQL array element.");
            }
            ValidateNullableLength(BinaryPrimitives.ReadInt32BigEndian(records));
            if (records.Length < RecordSize)
            {
                throw new InvalidDataException("Truncated PostgreSQL array element.");
            }
            records = records[RecordSize..];
        }
        if (!records.IsEmpty)
        {
            throw new InvalidDataException("Unexpected trailing bigint[] bytes.");
        }
    }

    private static void ValidateNullableRecords(SequenceReader<byte> reader,
        int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (!reader.TryReadBigEndian(out int length))
            {
                throw new InvalidDataException("Truncated PostgreSQL array element.");
            }
            ValidateNullableLength(length);
            if (reader.Remaining < sizeof(long))
            {
                throw new InvalidDataException("Truncated PostgreSQL array element.");
            }
            reader.Advance(sizeof(long));
        }
        if (reader.Remaining != 0)
        {
            throw new InvalidDataException("Unexpected trailing bigint[] bytes.");
        }
    }

    private static void ValidateNullableLength(int length)
    {
        if (length == -1)
        {
            throw new NotSupportedException("ReadOnlyMemory<long> cannot represent PostgreSQL array NULL elements.");
        }
        if (length != sizeof(long))
        {
            throw new InvalidDataException("A non-NULL bigint[] element must have length 8.");
        }
    }

    private static void ReadSegmentedRecords(ref SequenceReader<byte> reader,
        Span<long> destination)
    {
        int written = 0;
        while (written < destination.Length)
        {
            // Decode whole records within a segment through the same contiguous fast path.
            int count = Math.Min(reader.UnreadSpan.Length / RecordSize,
                destination.Length - written);
            if (count != 0)
            {
                int byteCount = count * RecordSize;
                ReadRecords(reader.UnreadSpan[..byteCount],
                    destination.Slice(written,
                        count));
                reader.Advance(byteCount);
                written += count;
            }
            else
            {
                // The sequence reader consumes split fields without copying the whole record first.
                if (!reader.TryReadBigEndian(out int length) || length != sizeof(long) ||
                    !reader.TryReadBigEndian(out long item))
                {
                    throw new InvalidDataException("Invalid or truncated PostgreSQL array element.");
                }
                destination[written++] = item;
            }
        }
    }

    private static void RequireCapacity(int count,
        int capacity)
    {
        if (capacity < count)
        {
            throw new ArgumentException("The destination is too small for the bigint[] elements.",
                "destination");
        }
    }

    private static void RequireSeparateStorage(ReadOnlySpan<byte> source,
        Span<byte> destination)
    {
        if (source.Overlaps(destination))
        {
            throw new ArgumentException("Array input and output storage must not overlap.",
                "destination");
        }
    }
}