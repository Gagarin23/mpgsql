using System.Buffers.Binary;
using Mpgsql.Protocol;

namespace Mpgsql.Internal;

// One reservation/publication for an optional Parse, Bind, portal Describe and unlimited Execute.
// UTF-8 C strings; all lengths, counts, OIDs and values are big-endian.
internal static class QueryPacket
{
    internal static int GetByteCount(
        string sql,
        ReadOnlySpan<MpgsqlParameterValue> parameters)
    {
        var size = checked(5 + 1 + WireEncoding.CStringLength(sql) + 2 + 4 * parameters.Length);
        return checked(size + GetPreparedByteCount("", parameters));
    }

    internal static int GetPreparedByteCount(string statement,
        ReadOnlySpan<MpgsqlParameterValue> parameters)
    {
        FrontendSize.Count(parameters.Length);
        // Bind: tag/length, unnamed portal, statement C string, binary format codes and counts.
        var size = checked(5 + 1 + WireEncoding.CStringLength(statement) + 4 + 2 + 4 + 7 + 10);
        foreach (ref readonly var parameter in parameters)
        {
            size = checked(size + 4 + Math.Max(0,
                parameter.PayloadLength));
        }
        return size;
    }

    internal static int Write(string sql,
        ReadOnlySpan<MpgsqlParameterValue> parameters,
        Span<byte> destination)
    {
        return WriteMeasured(sql, parameters, destination, GetByteCount(sql, parameters));
    }

    // Admission already validated the immutable SQL and borrowed parameter sizes. Write directly
    // and fill the Parse length after encoding SQL instead of repeating UTF-8/array sizing walks.
    internal static int WriteMeasured(string sql,
        ReadOnlySpan<MpgsqlParameterValue> parameters,
        Span<byte> destination,
        int size)
    {
        if (destination.Length < size)
        {
            throw new ArgumentException("The destination is too small for the complete query packet.",
                nameof(destination));
        }

        destination = destination[..size];
        destination[0] = (byte)'P';
        destination[5] = 0; // unnamed statement
        var offset = 6;
        offset += WireEncoding.Utf8.GetBytes(sql.AsSpan(), destination[offset..]);
        destination[offset++] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(destination[offset..], checked((ushort)parameters.Length));
        offset += 2;
        foreach (ref readonly var parameter in parameters)
        {
            BinaryPrimitives.WriteUInt32BigEndian(destination[offset..], parameter.PostgresTypeOid);
            offset += 4;
        }
        var parseSize = offset;
        BinaryPrimitives.WriteInt32BigEndian(destination[1..], parseSize - 1);

        return parseSize + WritePreparedCore("", parameters, destination[parseSize..], size - parseSize);
    }

    internal static int WritePrepared(string statement,
        ReadOnlySpan<MpgsqlParameterValue> parameters,
        Span<byte> destination)
    {
        return WritePreparedMeasured(statement, parameters, destination, GetPreparedByteCount(statement, parameters));
    }

    internal static int WritePreparedMeasured(string statement,
        ReadOnlySpan<MpgsqlParameterValue> parameters,
        Span<byte> destination,
        int size)
    {
        if (destination.Length < size)
        {
            throw new ArgumentException("The destination is too small for the complete prepared query packet.", nameof(destination));
        }
        return WritePreparedCore(statement, parameters, destination, size);
    }

    private static int WritePreparedCore(string statement,
        ReadOnlySpan<MpgsqlParameterValue> parameters,
        Span<byte> destination,
        int size)
    {
        var bindSize = size - 7 - 10;
        destination = destination[..size];
        var offset = 0;
        destination[offset] = (byte)'B';
        BinaryPrimitives.WriteInt32BigEndian(destination[(offset + 1)..],
            bindSize - 1);
        offset += 5;
        destination[offset++] = 0; // unnamed portal
        offset += WireEncoding.Utf8.GetBytes(statement.AsSpan(), destination[offset..]);
        destination[offset++] = 0; // statement C string terminator
        BinaryPrimitives.WriteUInt16BigEndian(destination[offset..],
            1);
        offset += 2;
        BinaryPrimitives.WriteInt16BigEndian(destination[offset..],
            1);
        offset += 2; // all parameters binary
        BinaryPrimitives.WriteUInt16BigEndian(destination[offset..],
            (ushort)parameters.Length);
        offset += 2;
        foreach (ref readonly var parameter in parameters)
        {
            var lengthOffset = offset;
            offset += 4;
            if (parameter.IsNull)
            {
                BinaryPrimitives.WriteInt32BigEndian(destination[lengthOffset..], -1);
                continue;
            }
            var length = parameter.WritePayload(destination[offset..]);
            BinaryPrimitives.WriteInt32BigEndian(destination[lengthOffset..], length);
            offset += length;
        }
        BinaryPrimitives.WriteUInt16BigEndian(destination[offset..],
            1);
        offset += 2;
        BinaryPrimitives.WriteInt16BigEndian(destination[offset..],
            1);
        offset += 2; // all results binary
        offset += FrontendMessage.Describe(StatementOrPortal.Portal).Write(destination[offset..]);
        offset += FrontendMessage.Execute().Write(destination[offset..]);
        return offset;
    }
}