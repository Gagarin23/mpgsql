using System.Buffers.Binary;
using Mpgsql.Protocol;

namespace Mpgsql.Internal;

// One reservation/publication for Parse, Bind, portal Describe and unlimited Execute.
// UTF-8 C strings; all lengths, counts, OIDs and values are big-endian.
internal static class QueryPacket
{
    internal static int GetByteCount(string sql,
        ReadOnlySpan<MpgsqlParameter> parameters)
    {
        FrontendSize.Count(parameters.Length);
        int size = checked(5 + 1 + WireEncoding.CStringLength(sql) + 2 + 4 * parameters.Length);
        size = checked(size + 5 + 2 + 4 + 2 + 4 + 7 + 10);
        foreach (ref readonly var parameter in parameters)
            size = checked(size + 4 + Math.Max(0,
                parameter.PayloadLength));
        return size;
    }

    internal static int Write(string sql,
        ReadOnlySpan<MpgsqlParameter> parameters,
        Span<byte> destination)
    {
        int size = GetByteCount(sql,
            parameters);
        if (destination.Length < size)
        {
            throw new ArgumentException("The destination is too small for the complete query packet.",
                nameof(destination));
        }

        int parseSize = checked(5 + 1 + WireEncoding.CStringLength(sql) + 2 + 4 * parameters.Length);
        var parse = new WireWriter(destination[..parseSize]);
        parse.Byte((byte)'P');
        parse.Int32(parseSize - 1);
        parse.Byte(0); // unnamed statement
        parse.CString(sql);
        parse.Count(parameters.Length);
        foreach (ref readonly var parameter in parameters) parse.UInt32(parameter.Oid);

        int bindSize = size - parseSize - 7 - 10;
        int offset = parseSize;
        destination[offset] = (byte)'B';
        BinaryPrimitives.WriteInt32BigEndian(destination[(offset + 1)..],
            bindSize - 1);
        offset += 5;
        destination[offset++] = 0; // unnamed portal
        destination[offset++] = 0; // unnamed statement
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
            int length = parameter.PayloadLength;
            BinaryPrimitives.WriteInt32BigEndian(destination[offset..],
                length);
            offset += 4;
            if (length >= 0)
            {
                parameter.WritePayload(destination.Slice(offset,
                    length));
                offset += length;
            }
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