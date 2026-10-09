using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Mpgsql.Benchmarks.Queries;
using Mpgsql.Internal;

namespace Mpgsql.Benchmarks.Comparison;

internal static class TcpAdoCoalescedVerification
{
    // Independent complete wire frames: ParseComplete, BindComplete,
    // c0 binary bigint RowDescription, bigint value 1, SELECT 1, Ready/idle.
    private static readonly byte[] OneReply = Convert.FromHexString(
        "31000000043200000004"
        + "54" + "0000001B" + "0001" + "633000" + "00000000" + "0000" + "00000014" + "0008" + "FFFFFFFF" + "0001"
        + "44000000120001000000080000000000000001"
        + "430000000D53454C454354203100");
    private static readonly byte[] Ready = Convert.FromHexString("5A0000000549");

    internal static async Task RunAsync()
    {
        await VerifyBoundaryAsync(flushBeforeSync: false).ConfigureAwait(false);
        await VerifyBoundaryAsync(flushBeforeSync: true).ConfigureAwait(false);
        foreach (var native in new[] { false, true })
        foreach (var scenario in QueryScenario.Readers)
        {
            var benchmark = new TcpAdoReaderBenchmarks
            {
                Case = scenario.Name, InstrumentTransport = false, CoalesceReplies = true
            };
            try
            {
                if (native) await benchmark.SetupNpgsql().ConfigureAwait(false);
                else await benchmark.Setup().ConfigureAwait(false);
                benchmark.CheckIdle();
                var before = benchmark.Peer.Counters();
                var sum = native ? await benchmark.NpgsqlReusedTyped().ConfigureAwait(false)
                    : await benchmark.ReusedTyped().ConfigureAwait(false);
                benchmark.CheckIdle();
                var after = benchmark.Peer.Counters();
                Check(sum == scenario.Expected(0), "Coalesced reader checksum: " + scenario.Name);
                Check(after.Queries - before.Queries == 1 && after.Syncs - before.Syncs == 1
                    && after.Connections == before.Connections, "Coalesced reader query/Sync/connection counts");
            }
            finally { await benchmark.Cleanup().ConfigureAwait(false); }
        }
        foreach (var native in new[] { false, true })
        {
            var benchmark = new TcpFacadeBatchComparisonBenchmarks
            {
                InstrumentTransport = false, CoalesceReplies = true
            };
            try
            {
                if (native)
                {
                    await benchmark.SetupNpgsql().ConfigureAwait(false);
                    benchmark.PrepareNpgsql();
                }
                else
                {
                    await benchmark.SetupMpgsql().ConfigureAwait(false);
                    benchmark.PrepareMpgsql();
                }
                var before = benchmark.Peer.Counters();
                var sum = native ? await benchmark.NpgsqlFreshBatch().ConfigureAwait(false)
                    : await benchmark.MpgsqlFacadeBatch().ConfigureAwait(false);
                benchmark.CheckIdle();
                var after = benchmark.Peer.Counters();
                Check(sum == 136L * 32, "Coalesced Batch16 checksum");
                Check(after.Queries - before.Queries == 16L * 32 && after.Syncs - before.Syncs == 32
                    && after.Connections == before.Connections, "Coalesced batch query/Sync/connection counts");
            }
            finally { await benchmark.Cleanup().ConfigureAwait(false); }
        }
        Console.WriteLine("Coalesced ADO diagnostic verification: complete Sync-only and Flush/Sync transcripts; five readers and Batch16 through both providers passed.");
    }

    private static async Task VerifyBoundaryAsync(bool flushBeforeSync)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        var catalog = new QueryCatalog([QueryScenario.One], 1);
        await using var peer = new TcpQueryPeer(new TcpQueryCatalog(catalog), coalesceReplies: true);
        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, peer.Port, token).ConfigureAwait(false);
        var stream = client.GetStream();
        var startupPayload = Encoding.UTF8.GetBytes("user\0benchmark\0database\0benchmark\0\0");
        var startup = new byte[8 + startupPayload.Length];
        BinaryPrimitives.WriteInt32BigEndian(startup, startup.Length);
        BinaryPrimitives.WriteInt32BigEndian(startup.AsSpan(4), 196608);
        startupPayload.CopyTo(startup.AsSpan(8));
        await stream.WriteAsync(startup, token).ConfigureAwait(false);
        var startupReply = new byte[TcpQueryCatalog.StartupReply.Length];
        await stream.ReadExactlyAsync(startupReply, token).ConfigureAwait(false);
        Check(startupReply.AsSpan().SequenceEqual(TcpQueryCatalog.StartupReply), "Complete immediate startup response");
        var frontend = ExpectedQuery();
        var encoded = new byte[QueryPacket.GetByteCount(QueryScenario.One.Sql, catalog.Inputs[0][0])];
        Check(QueryPacket.Write(QueryScenario.One.Sql, catalog.Inputs[0][0], encoded) == encoded.Length
            && encoded.AsSpan().SequenceEqual(frontend), "Complete independent frontend P/B/D/E transcript");
        await stream.WriteAsync(frontend, token).ConfigureAwait(false);
        while (peer.Counters().Queries != 1)
        {
            peer.CheckHealthy();
            await Task.Delay(1, token).ConfigureAwait(false);
        }
        var first = new byte[1];
        var pending = stream.ReadExactlyAsync(first, token).AsTask();
        await Task.Delay(50, token).ConfigureAwait(false);
        Check(!pending.IsCompleted, "P/B/D/E prefix must remain unflushed before a frontend boundary");
        var boundary = Convert.FromHexString(flushBeforeSync ? "4800000004" : "5300000004");
        await stream.WriteAsync(boundary, token).ConfigureAwait(false);
        await pending.ConfigureAwait(false);
        byte[] expected = flushBeforeSync ? OneReply : [.. OneReply, .. Ready];
        var reply = new byte[expected.Length];
        reply[0] = first[0];
        await stream.ReadExactlyAsync(reply.AsMemory(1), token).ConfigureAwait(false);
        Check(reply.AsSpan().SequenceEqual(expected), "Complete coalesced reply payload");
        if (flushBeforeSync)
        {
            // Empty Flush response still publishes all accumulated replies.
            await stream.WriteAsync(Convert.FromHexString("5300000004"), token).ConfigureAwait(false);
            var ready = new byte[Ready.Length];
            await stream.ReadExactlyAsync(ready, token).ConfigureAwait(false);
            Check(ready.AsSpan().SequenceEqual(Ready), "Separate Sync/Ready payload after Flush");
        }
        peer.CheckHealthy();
        var counters = peer.Counters();
        Check(counters.Queries == 1 && counters.Syncs == 1 && counters.Connections == 1
            && counters.ReplyBytes == startupReply.Length + OneReply.Length + Ready.Length,
            "Complete byte/query/Sync/connection counters");
    }

    private static byte[] ExpectedQuery()
    {
        var output = new ArrayBufferWriter<byte>();
        var parse = new ArrayBufferWriter<byte>();
        parse.Write<byte>([0]);
        parse.Write(Encoding.UTF8.GetBytes(QueryScenario.One.Sql + '\0'));
        parse.Write(Convert.FromHexString("000100000014")); // one bigint OID, BE
        var frame = new byte[parse.WrittenCount + 5];
        frame[0] = (byte)'P';
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1), parse.WrittenCount + 4);
        parse.WrittenSpan.CopyTo(frame.AsSpan(5));
        output.Write(frame);
        // Empty names, one binary format, one 8-byte bigint value, binary result.
        output.Write(Convert.FromHexString("42" + "0000001C" + "0000" + "0001" + "0001" + "0001" + "00000008"
            + "0000000000000001" + "0001" + "0001"));
        output.Write(Convert.FromHexString("44000000065000")); // Describe portal
        output.Write(Convert.FromHexString("45000000090000000000")); // Execute unlimited
        return output.WrittenSpan.ToArray();
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }
}
