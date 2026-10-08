using System.Buffers;
using System.Buffers.Binary;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL bigint conversion for long and long?.</summary>
/// <remarks>
///     A non-NULL payload is exactly eight big-endian bytes. Bind/DataRow/COPY own
///     the outer length and SQL NULL marker; this converter writes only the payload.
///     NULL writes zero bytes and must be marked NULL by the caller's field encoder.
///     An empty, non-NULL payload is invalid, rather than another representation of NULL.
/// </remarks>
public static class Int64Converter
{
    public const uint TypeOid = (uint)Mpgsql.TypeOid.Int64;
    public const int ByteCount = sizeof(long);

    public static int GetByteCount(long value)
    {
        return ByteCount;
    }

    /// <summary>Returns eight for a value, zero for SQL NULL's absent payload.</summary>
    public static int GetByteCount(long? value)
    {
        return value.HasValue ? ByteCount : 0;
    }

    /// <summary>Writes eight bytes and returns the number written.</summary>
    /// <remarks>Insufficient capacity throws ArgumentException before changing the destination.</remarks>
    public static int Write(
        long value,
        Span<byte> destination
    )
    {
        if (!BinaryPrimitives.TryWriteInt64BigEndian
            (
                destination,
                value
            ))
        {
            throw new ArgumentException
            (
                "The destination is too small for the bigint payload.",
                nameof(destination)
            );
        }
        return ByteCount;
    }

    /// <summary>Writes a value, or leaves the destination unchanged and returns zero for NULL.</summary>
    public static int Write(
        long? value,
        Span<byte> destination
    )
    {
        return value.HasValue
            ? Write
            (
                value.GetValueOrDefault(),
                destination
            )
            : 0;
    }

    /// <summary>Reserves and advances exactly eight payload bytes.</summary>
    public static void Write(
        long value,
        IBufferWriter<byte> destination
    )
    {
        ArgumentNullException.ThrowIfNull(destination);
        Write
        (
            value,
            destination.GetSpan(ByteCount)
        );
        destination.Advance(ByteCount);
    }

    /// <summary>Writes a value with one reservation; NULL neither reserves nor advances.</summary>
    public static void Write(
        long? value,
        IBufferWriter<byte> destination
    )
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (value.HasValue)
        {
            Write
            (
                value.GetValueOrDefault(),
                destination
            );
        }
    }

    /// <summary>Reads exactly eight big-endian bytes; rejects truncated or trailing bytes.</summary>
    public static long Read(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != ByteCount)
        {
            throw new InvalidDataException("A binary bigint requires exactly eight bytes.");
        }
        return BinaryPrimitives.ReadInt64BigEndian(payload);
    }

    /// <summary>Reads a borrowed, possibly segmented field without allocating a payload buffer.</summary>
    public static long Read(ReadOnlySequence<byte> payload)
    {
        if (payload.Length != ByteCount)
        {
            throw new InvalidDataException("A binary bigint requires exactly eight bytes.");
        }
        if (payload.IsSingleSegment)
        {
            return BinaryPrimitives.ReadInt64BigEndian(payload.FirstSpan);
        }
        // Exactly eight bytes are guaranteed above. Accumulate in wire order instead of
        // constructing a SequenceReader and copying a split value into its scratch buffer.
        long result = 0;
        foreach (var segment in payload)
        foreach (var part in segment.Span)
        {
            result = result << 8 | part;
        }
        return result;
    }

    /// <summary>Uses the outer field's nullable memory to distinguish SQL NULL from payload bytes.</summary>
    public static long? ReadNullable(ReadOnlyMemory<byte>? payload)
    {
        return payload is { } value ? Read(value.Span) : null;
    }

    /// <summary>Uses the outer field's nullable sequence to distinguish SQL NULL from payload bytes.</summary>
    public static long? ReadNullable(ReadOnlySequence<byte>? payload)
    {
        return payload is { } value ? Read(value) : null;
    }
}