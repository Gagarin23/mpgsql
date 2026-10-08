using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Mpgsql.Benchmarks.Queries;

namespace Mpgsql.Benchmarks.Comparison;

internal sealed class TcpQueryCatalog
{
    internal QueryCatalog Queries { get; }
    internal ReadOnlyMemory<byte>[] Descriptions { get; }
    internal ReadOnlyMemory<byte>[][] Executions { get; }
    internal static readonly byte[] ParseComplete = QueryWire.Frame('1', []);
    internal static readonly byte[] BindComplete = QueryWire.Frame('2', []);
    internal static readonly byte[] StartupReply = CreateStartup();

    internal TcpQueryCatalog(QueryCatalog queries)
    {
        Queries = queries;
        Descriptions = new ReadOnlyMemory<byte>[queries.Scenarios.Length];
        Executions = new ReadOnlyMemory<byte>[queries.Scenarios.Length][];
        for (int i = 0; i < Descriptions.Length; i++)
        {
            byte[] first = queries.Replies[i][0];
            int descriptionLength = 1 + BinaryPrimitives.ReadInt32BigEndian(first.AsSpan(11));
            Descriptions[i] = first.AsMemory(10, descriptionLength);
            Executions[i] = new ReadOnlyMemory<byte>[queries.Replies[i].Length];
            for (int worker = 0; worker < Executions[i].Length; worker++)
                if (queries.Replies[i][worker].Length != 0)
                {
                    Executions[i][worker] = queries.Replies[i][worker].AsMemory(10 + descriptionLength);
                }
        }
    }

    private static byte[] CreateStartup()
    {
        var output = new ArrayBufferWriter<byte>();
        output.Write(QueryWire.Frame('R', new byte[4])); // AuthenticationOk, Int32 BE = 0
        foreach (var pair in new[] {("server_version", "17.0"), ("server_encoding", "UTF8"),
                     ("client_encoding", "UTF8"), ("DateStyle", "ISO, MDY"), ("TimeZone", "UTC"),
                     ("integer_datetimes", "on"), ("standard_conforming_strings", "on")})
            output.Write(QueryWire.Frame('S', Encoding.UTF8.GetBytes(pair.Item1 + '\0' + pair.Item2 + '\0')));
        byte[] key = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(key, 12345);
        BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(4), 67890);
        output.Write(QueryWire.Frame('K', key));
        output.Write(QueryWire.Ready);
        return output.WrittenSpan.ToArray();
    }
}
