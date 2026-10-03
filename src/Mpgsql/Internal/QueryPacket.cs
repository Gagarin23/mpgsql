using System.Buffers.Binary;
using Mpgsql.Protocol;

namespace Mpgsql.Internal;

// One reservation/publication for an optional Parse, Bind, portal Describe and unlimited Execute.
// UTF-8 C strings; all lengths, counts, OIDs and values are big-endian.
internal static class QueryPacket
{
    internal static int GetByteCount(
        string sql,
        ReadOnlySpan<MpgsqlParameter> parameters)
    {
        int size = checked(5 + 1 + WireEncoding.CStringLength(sql) + 2 + 4 * parameters.Length);
        return checked(size + GetPreparedByteCount("", parameters));
    }

    internal static int GetPreparedByteCount(string statement,
        ReadOnlySpan<MpgsqlParameter> parameters)
    {
        FrontendSize.Count(parameters.Length);
        // Bind: tag/length, unnamed portal, statement C string, binary format codes and counts.
        int size = checked(5 + 1 + WireEncoding.CStringLength(statement) + 4 + 2 + 4 + 7 + 10);
        foreach (ref readonly var parameter in parameters)
            size = checked(size + 4 + Math.Max(0,
                parameter.PayloadLength));
        return size;
    }

    internal static int Write(string sql,
        ReadOnlySpan<MpgsqlParameter> parameters,
        Span<byte> destination)
    {
        int parseSize = checked(5 + 1 + WireEncoding.CStringLength(sql) + 2 + 4 * parameters.Length);
        int size = checked(parseSize + GetPreparedByteCount("", parameters));
        if (destination.Length < size)
        {
            throw new ArgumentException("The destination is too small for the complete query packet.",
                nameof(destination));
        }

        var parse = new WireWriter(destination[..parseSize]);
        parse.Byte((byte)'P');
        parse.Int32(parseSize - 1);
        parse.Byte(0); // unnamed statement
        parse.CString(sql);
        parse.Count(parameters.Length);
        foreach (ref readonly var parameter in parameters) parse.UInt32(parameter.PostgresTypeOid);

        return parseSize + WritePreparedCore("", parameters, destination[parseSize..], size - parseSize);
    }

    internal static int WritePrepared(string statement,
        ReadOnlySpan<MpgsqlParameter> parameters,
        Span<byte> destination)
    {
        int size = GetPreparedByteCount(statement, parameters);
        if (destination.Length < size)
            throw new ArgumentException("The destination is too small for the complete prepared query packet.", nameof(destination));
        return WritePreparedCore(statement, parameters, destination, size);
    }

    private static int WritePreparedCore(string statement,
        ReadOnlySpan<MpgsqlParameter> parameters,
        Span<byte> destination,
        int size)
    {
        int bindSize = size - 7 - 10;
        destination = destination[..size];
        int offset = 0;
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
            int lengthOffset = offset;
            offset += 4;
            if (parameter.IsNull)
            {
                BinaryPrimitives.WriteInt32BigEndian(destination[lengthOffset..], -1);
                continue;
            }
            int length = parameter.WritePayload(destination[offset..]);
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
