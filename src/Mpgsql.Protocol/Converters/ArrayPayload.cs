using System.Buffers;
using System.Buffers.Binary;

namespace Mpgsql.Converters;

internal static class ArrayPayload
{
    internal const int EmptyHeaderSize = 12;
    internal const int HeaderSize = 20;

    internal static int Measure(int count, int elementBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return count == 0 ? EmptyHeaderSize : checked(HeaderSize + 4 * count + elementBytes);
    }

    internal static void WriteHeader(
        Span<byte> bytes, int count,
        bool hasNull, uint oid
    )
    {
        // Int32 ndim, Int32 flags, UInt32 element OID; Int32 count/lower bound for ndim=1.
        BinaryPrimitives.WriteInt32BigEndian(bytes, count == 0 ? 0 : 1);
        BinaryPrimitives.WriteInt32BigEndian(bytes[4..], hasNull ? 1 : 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes[8..], oid);
        if (count == 0)
        {
            return;
        }
        BinaryPrimitives.WriteInt32BigEndian(bytes[12..], count);
        BinaryPrimitives.WriteInt32BigEndian(bytes[16..], 1);
    }

    internal static int ReadHeader(
        ReadOnlySpan<byte> bytes, uint oid,
        int fixedSize, out int headerSize
    )
    {
        if (bytes.Length < EmptyHeaderSize)
        {
            throw new InvalidDataException("Truncated PostgreSQL array header.");
        }
        var dimensions = BinaryPrimitives.ReadInt32BigEndian(bytes);
        ValidateType
        (
            dimensions, BinaryPrimitives.ReadInt32BigEndian(bytes[4..]),
            BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]), oid
        );
        headerSize = dimensions == 0 ? EmptyHeaderSize : HeaderSize;
        if (bytes.Length < headerSize)
        {
            throw new InvalidDataException("Truncated PostgreSQL array dimension.");
        }
        var count = dimensions == 0 ? 0 : BinaryPrimitives.ReadInt32BigEndian(bytes[12..]);
        var lowerBound = dimensions == 0 ? 0 : BinaryPrimitives.ReadInt32BigEndian(bytes[16..]);
        ValidateLength(count, lowerBound, bytes.Length - headerSize, fixedSize);
        return count;
    }

    internal static int ReadHeader(
        ref SequenceReader<byte> reader, uint oid,
        int fixedSize
    )
    {
        if (!reader.TryReadBigEndian(out int dimensions) || !reader.TryReadBigEndian(out int flags) ||
            !reader.TryReadBigEndian(out int elementOid))
        {
            throw new InvalidDataException("Truncated PostgreSQL array header.");
        }
        ValidateType(dimensions, flags, unchecked((uint)elementOid), oid);
        int count = 0,
            lowerBound = 0;
        if (dimensions == 1 && (!reader.TryReadBigEndian(out count) || !reader.TryReadBigEndian(out lowerBound)))
        {
            throw new InvalidDataException("Truncated PostgreSQL array dimension.");
        }
        ValidateLength(count, lowerBound, reader.Remaining, fixedSize);
        return count;
    }

    private static void ValidateType(
        int dimensions, int flags,
        uint actual, uint expected
    )
    {
        if ((uint)dimensions > 6 || (uint)flags > 1 || actual != expected)
        {
            throw new InvalidDataException("Invalid PostgreSQL array dimensions, flags, or element OID.");
        }
        if (dimensions > 1)
        {
            throw new NotSupportedException("ReadOnlyMemory<T> cannot represent multidimensional PostgreSQL arrays.");
        }
    }

    private static void ValidateLength(
        int count, int lowerBound,
        long remaining, int fixedSize
    )
    {
        if (count < 0 || (long)lowerBound + count > int.MaxValue || remaining < 4L * count ||
            count == 0 && remaining != 0 || fixedSize != 0 && remaining > (4L + fixedSize) * count)
        {
            throw new InvalidDataException("The PostgreSQL array bounds or payload length are invalid.");
        }
        // Bound the result allocation by actual input, even for variable-length elements.
        // flags is advisory: actual NULLs are identified solely by a -1 element length.
    }

    internal static int ReadLength(ref SequenceReader<byte> reader)
    {
        if (!reader.TryReadBigEndian(out int length) || length < -1 || length > reader.Remaining)
        {
            throw new InvalidDataException("Invalid or truncated PostgreSQL array element.");
        }
        return length;
    }

    internal static int ReadLength(ReadOnlySpan<byte> records)
    {
        if (records.Length < 4)
        {
            throw new InvalidDataException("Truncated PostgreSQL array element length.");
        }
        var length = BinaryPrimitives.ReadInt32BigEndian(records);
        if (length < -1 || length > records.Length - 4)
        {
            throw new InvalidDataException("Invalid or truncated PostgreSQL array element.");
        }
        return length;
    }
}