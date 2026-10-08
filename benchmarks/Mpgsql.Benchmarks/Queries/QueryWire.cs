using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Mpgsql.Benchmarks.Queries;

internal static class QueryWire
{
    internal static readonly byte[] Ready = Frame('Z', [(byte)'I']);

    internal static byte[] Frame(char tag, ReadOnlySpan<byte> payload)
    {
        var bytes = new byte[5 + payload.Length];
        bytes[0] = (byte)tag;
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(1), payload.Length + 4);
        payload.CopyTo(bytes.AsSpan(5));
        return bytes;
    }

    internal static byte[] Reply(QueryScenario scenario, int worker)
    {
        var output = new ArrayBufferWriter<byte>();
        output.Write(Frame('1', [])); // ParseComplete
        output.Write(Frame('2', [])); // BindComplete
        output.Write(scenario.NoData ? Frame('n', []) : Description(scenario));
        if (scenario.Error)
        {
            output.Write(Frame('E', Encoding.UTF8.GetBytes("SERROR\0C22012\0Msynthetic division by zero\0\0")));
            return output.WrittenSpan.ToArray();
        }
        for (var row = 0; row < scenario.Rows; row++)
        {
            var payload = new ArrayBufferWriter<byte>();
            UInt16(payload, (ushort)scenario.Columns);
            for (var column = 0; column < scenario.Columns; column++)
            {
                if (scenario.Null)
                {
                    Int32(payload, -1);
                }
                else if (scenario.ByteaBytes != 0 && column == 1)
                {
                    Int32(payload, scenario.ByteaBytes);
                    payload.Write(scenario.Blob);
                }
                else
                {
                    Int32(payload, 8);
                    var integers = scenario.ByteaBytes == 0 ? scenario.Columns : 1;
                    BinaryPrimitives.WriteInt64BigEndian(payload.GetSpan(8), worker + 1L + (long)row * integers + column);
                    payload.Advance(8);
                }
            }
            output.Write(Frame('D', payload.WrittenSpan));
        }
        var tag = scenario.NoData || scenario.Returning ? $"UPDATE {scenario.Rows}" : $"SELECT {scenario.Rows}";
        output.Write(Frame('C', Encoding.UTF8.GetBytes(tag + '\0')));
        return output.WrittenSpan.ToArray();
    }

    internal static byte[] Description(QueryScenario scenario)
    {
        var payload = new ArrayBufferWriter<byte>();
        UInt16(payload, (ushort)scenario.Columns);
        for (var column = 0; column < scenario.Columns; column++)
        {
            payload.Write(Encoding.UTF8.GetBytes("c" + column + '\0'));
            Int32(payload, 0); // table OID
            UInt16(payload, 0); // attribute
            var blob = scenario.ByteaBytes != 0 && column == 1;
            Int32(payload, blob ? 17 : 20);
            UInt16(payload, blob ? ushort.MaxValue : (ushort)8);
            Int32(payload, -1); // type modifier
            UInt16(payload, 1); // binary
        }
        return Frame('T', payload.WrittenSpan);
    }

    private static void UInt16(IBufferWriter<byte> output, ushort value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(output.GetSpan(2), value);
        output.Advance(2);
    }
    private static void Int32(IBufferWriter<byte> output, int value)
    {
        BinaryPrimitives.WriteInt32BigEndian(output.GetSpan(4), value);
        output.Advance(4);
    }

    internal static bool Equal(ReadOnlySequence<byte> bytes, ReadOnlySpan<byte> expected)
    {
        if (bytes.Length != expected.Length)
        {
            return false;
        }
        foreach (var segment in bytes)
        {
            if (!segment.Span.SequenceEqual(expected[..segment.Length]))
            {
                return false;
            }
            expected = expected[segment.Length..];
        }
        return true;
    }
}