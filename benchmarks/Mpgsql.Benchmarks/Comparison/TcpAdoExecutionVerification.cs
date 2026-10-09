using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Mpgsql.Benchmarks.Queries;
using Mpgsql.Internal;
using Mpgsql.Protocol;

namespace Mpgsql.Benchmarks.Comparison;

internal static class TcpAdoExecutionVerification
{
    internal static readonly string[] Cases =
    ["ParameterMutation32", "ScalarEmpty", "ScalarOneBigint", "ScalarNull", "ScalarRows128", "ReusedBatch16", "NonQueryZero"];
    private static readonly byte[] BigintDescription = Convert.FromHexString(
        "54" + "0000001B" + "0001" + "633000" + "00000000" + "0000" + "00000014" + "0008" + "FFFFFFFF" + "0001");

    internal static async Task RunAsync()
    {
        VerifyWire(QueryScenario.One, Enumerable.Range(1, 32).ToArray(), 1);
        VerifyWire(QueryScenario.One, Enumerable.Range(0, 32).SelectMany(_ => Enumerable.Range(1, 16)).ToArray(), 16);
        foreach (var scenario in new[] { QueryScenario.Empty, QueryScenario.One, QueryScenario.NullValue, QueryScenario.ScalarMany, QueryScenario.NonQuery })
            VerifyWire(scenario, [1], 1);
        foreach (var native in new[] { false, true })
        foreach (var name in Cases)
        {
            var benchmark = new TcpAdoExecutionBenchmarks { Case = name, InstrumentTransport = false };
            try
            {
                if (native) await benchmark.SetupNpgsql().ConfigureAwait(false);
                else await benchmark.SetupMpgsql().ConfigureAwait(false);
                for (var repeat = 0; repeat < 2; repeat++)
                {
                    benchmark.CheckIdle();
                    var before = benchmark.Peer.Counters();
                    var result = native ? await benchmark.NpgsqlExecute().ConfigureAwait(false)
                        : await benchmark.MpgsqlExecute().ConfigureAwait(false);
                    benchmark.CheckIdle();
                    var after = benchmark.Peer.Counters();
                    Check(result == benchmark.ExpectedChecksum, "Execution checksum: " + name);
                    Check(after.Queries - before.Queries == (long)benchmark.OperationsPerInvocation * benchmark.QueriesPerOperation,
                        "Query count: " + name);
                    Check(after.Syncs - before.Syncs == (long)benchmark.OperationsPerInvocation * benchmark.SyncsPerOperation,
                        "Sync count: " + name);
                    Check(after.Connections == before.Connections, "Exclusive connector retained: " + name);
                }
            }
            finally { await benchmark.Cleanup().ConfigureAwait(false); }
        }
        Console.WriteLine("ADO execution verification: 7 exact wire transcripts; 7 cases x 2 providers x 2 executions passed.");
    }

    // Independent expected payloads include every tag, length, CString, OID,
    // format code, parameter byte and NULL length. Fragment each complete frame
    // before processing it, rather than depending on loopback TCP coalescing.
    private static void VerifyWire(QueryScenario scenario, int[] values, int queriesPerSync)
    {
        var queries = new QueryCatalog([scenario], 32);
        var protocol = new TcpQueryProtocol(new TcpQueryCatalog(queries), verifyPayloads: true);
        var actualFrontend = new ArrayBufferWriter<byte>();
        var expectedFrontend = new ArrayBufferWriter<byte>();
        var expectedBackend = new ArrayBufferWriter<byte>();
        for (var index = 0; index < values.Length; index++)
        {
            var value = values[index];
            var parameters = queries.Inputs[0][value - 1];
            var size = QueryPacket.GetByteCount(scenario.Sql, parameters);
            var written = QueryPacket.Write(scenario.Sql, parameters, actualFrontend.GetSpan(size));
            Check(written == size, "Complete query packet size");
            actualFrontend.Advance(written);
            ExpectedQuery(expectedFrontend, scenario.Sql, value);
            ExpectedReply(expectedBackend, scenario, value);
            if ((index + 1) % queriesPerSync == 0)
            {
                var syncBytes = FrontendMessage.Sync().Write(actualFrontend.GetSpan(5));
                Check(syncBytes == 5, "Complete Sync frame size");
                actualFrontend.Advance(syncBytes);
                expectedFrontend.Write(Convert.FromHexString("5300000004"));
                expectedBackend.Write(Convert.FromHexString("5A0000000549"));
            }
        }
        Check(actualFrontend.WrittenSpan.SequenceEqual(expectedFrontend.WrittenSpan), "Complete frontend payload: " + scenario.Name);
        var input = new ReadOnlySequence<byte>(actualFrontend.WrittenMemory);
        var actualBackend = new ArrayBufferWriter<byte>();
        while (FrontendFrameParser.TryRead(ref input, out var tag, out _, out var frame))
        {
            var split = Math.Max(1, (int)frame.Length / 2);
            var first = new Segment(frame.Slice(0, split).ToArray());
            var last = first.Append(frame.Slice(split).ToArray());
            var fragmented = new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
            Check(FrontendFrameParser.TryRead(ref fragmented, out var actualTag, out var payload, out _) && actualTag == tag && fragmented.IsEmpty,
                "Fragmented complete frontend frame");
            actualBackend.Write(protocol.Process(tag, payload).Span);
        }
        Check(input.IsEmpty && protocol.Queries == values.Length && protocol.Syncs == values.Length / queriesPerSync,
            "Wire query/Sync boundaries");
        Check(actualBackend.WrittenSpan.SequenceEqual(expectedBackend.WrittenSpan), "Complete backend payload: " + scenario.Name);
    }

    private static void ExpectedQuery(IBufferWriter<byte> output, string sql, long value)
    {
        var parse = new ArrayBufferWriter<byte>();
        parse.Write<byte>([0]); // unnamed statement
        parse.Write(Encoding.UTF8.GetBytes(sql + '\0'));
        parse.Write(Convert.FromHexString("000100000014")); // one OID, bigint 20, BE
        output.Write(Frame('P', parse.WrittenSpan));
        var bind = new ArrayBufferWriter<byte>();
        bind.Write(Convert.FromHexString("000000010001000100000008")); // names; binary; one value of length 8
        BinaryPrimitives.WriteInt64BigEndian(bind.GetSpan(8), value);
        bind.Advance(8);
        bind.Write(Convert.FromHexString("00010001")); // one binary result format
        output.Write(Frame('B', bind.WrittenSpan));
        output.Write(Convert.FromHexString("44000000065000")); // Describe portal ""
        output.Write(Convert.FromHexString("45000000090000000000")); // Execute portal "", unlimited
    }

    private static void ExpectedReply(IBufferWriter<byte> output, QueryScenario scenario, long value)
    {
        output.Write(Convert.FromHexString("31000000043200000004"));
        output.Write(scenario.NoData ? Convert.FromHexString("6E00000004") : BigintDescription);
        for (var row = 0; row < scenario.Rows; row++)
        {
            if (scenario.Null)
                output.Write(Convert.FromHexString("440000000A0001FFFFFFFF"));
            else
            {
                var data = new byte[14];
                data[1] = 1; // Int16 BE field count
                BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(2), 8);
                BinaryPrimitives.WriteInt64BigEndian(data.AsSpan(6), value + row);
                output.Write(Frame('D', data));
            }
        }
        output.Write(Frame('C', Encoding.UTF8.GetBytes((scenario.NoData ? "UPDATE " : "SELECT ") + scenario.Rows + '\0')));
    }

    private static byte[] Frame(char tag, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[payload.Length + 5];
        frame[0] = (byte)tag;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1), payload.Length + 4);
        payload.CopyTo(frame.AsSpan(5));
        return frame;
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        internal Segment(byte[] memory) { Memory = memory; }
        internal Segment Append(byte[] memory)
        {
            var next = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}
