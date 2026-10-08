using System.Buffers;
using System.Net;
using System.Net.Sockets;
using Mpgsql.Benchmarks.Queries;
using Mpgsql.Internal;
using Mpgsql.Protocol;
using Npgsql;

namespace Mpgsql.Benchmarks.Comparison;

internal static class TcpComparisonVerification
{
    internal static async Task RunAsync()
    {
        VerifyProtocol();
        var catalog = new QueryCatalog([QueryScenario.One, .. QueryScenario.Readers.Where(x => x != QueryScenario.One),
            QueryScenario.NullValue, QueryScenario.ScalarMany, QueryScenario.NonQuery, QueryScenario.ReturningRows, QueryScenario.Failing], 1);
        await using (var m = await TcpMpgsqlFixture.CreateAsync(catalog))
        {
            foreach (var scenario in catalog.Scenarios.Where(x => !x.Error))
            {
                await using (var reader = await m.Source.ExecuteReaderAsync(scenario.Sql, catalog.Inputs[catalog.Index(scenario)][0]))
                    await CheckReader(reader, scenario, m.Buffers[0]);
                Check(await TcpQueryOperations.MpgsqlAsync(m, scenario) == scenario.Expected(0), "Mpgsql DataSource repeated " + scenario.Name);
                Check(await TcpQueryOperations.RawAsync(m.Transports[0], catalog, scenario, m.Buffers[0]) == scenario.Expected(0), "Mpgsql raw " + scenario.Name);
                m.CheckIdle();
            }
            try { await TcpQueryOperations.MpgsqlAsync(m, QueryScenario.Failing); throw new InvalidOperationException("SQL error missing."); }
            catch (MpgsqlServerException error) { Check(error.SqlState == "22012", "Mpgsql SQLSTATE"); }
            Check(await TcpQueryOperations.MpgsqlAsync(m, QueryScenario.One) == 1, "Mpgsql recovery probe"); m.CheckIdle();
        }
        foreach (bool mux in new[] {false, true})
        {
            await using var n = await TcpNpgsqlFixture.CreateAsync(catalog, multiplexing: mux);
            foreach (var scenario in catalog.Scenarios.Where(x => !x.Error))
            {
                await using (var reader = await n.Commands[catalog.Index(scenario)][0].ExecuteReaderAsync())
                    await CheckReader(reader, scenario, n.Buffers[0]);
                Check(await TcpQueryOperations.NpgsqlAsync(n, catalog, scenario) == scenario.Expected(0), "Npgsql repeated " + scenario.Name);
                n.Peer.CheckHealthy();
            }
            try { await TcpQueryOperations.NpgsqlAsync(n, catalog, QueryScenario.Failing); throw new InvalidOperationException("SQL error missing."); }
            catch (PostgresException error) { Check(error.SqlState == "22012", "Npgsql SQLSTATE"); }
            Check(await TcpQueryOperations.NpgsqlAsync(n, catalog, QueryScenario.One) == 1, "Npgsql recovery probe");
        }
        foreach (string name in new[] {"ScalarEmpty", "ScalarNull", "ScalarOne", "ScalarRows128", "NonQuery", "ReturningRows128", "EarlyDispose4096"})
        foreach (var driver in Enum.GetValues<ComparisonDriver>())
        {
            var b = new TcpConsumptionComparisonBenchmarks {Case = name};
            try
            {
                if (driver == ComparisonDriver.Mpgsql) await b.SetupMpgsql();
                else if (driver == ComparisonDriver.NpgsqlPool) await b.SetupPool(); else await b.SetupMultiplexed();
                long expected = name switch {"ScalarEmpty" => -2, "ScalarNull" => -1, "NonQuery" => 0, "ReturningRows128" => 128, _ => 1};
                for (int repeat = 0; repeat < 2; repeat++)
                    Check(await (driver == ComparisonDriver.Mpgsql ? b.MpgsqlDataSource() : driver == ComparisonDriver.NpgsqlPool ? b.NpgsqlPool() : b.NpgsqlMultiplexed()) == expected, "Consumption " + name);
            }
            finally { await b.Cleanup(); }
        }
        await VerifyBatchAsync();
        await VerifyFacadeBatchAsync();
        await TcpLongBatchComparisonBenchmarks.VerifyAsync();
        await TcpLongFacadeBatchComparisonBenchmarks.VerifyAsync();
        await TcpBatchProfileRunner.VerifyAsync();
        await TcpPipelineProfileRunner.VerifyAsync();
        await VerifyPeerFailureAsync();
        await TcpPipelineWindowBenchmarks.VerifyAsync();
        await TcpSharedSyncBenchmarks.VerifyAsync();
        await TcpSharedSyncCohortBenchmarks.VerifyAsync();
        foreach (var profile in QueryLoadProfile.All)
        foreach (var driver in Enum.GetValues<ComparisonDriver>())
        {
            await using var fixture = await TcpComparisonFixture.CreateAsync(driver, profile);
            var before = fixture.Peer.Counters();
            var pending = new Task[profile.Callers];
            for (int worker = 0; worker < pending.Length; worker++) pending[worker] = WorkAsync(worker);
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(60));
            var after = fixture.Peer.Counters();
            Check(after.Queries - before.Queries == profile.Callers * 2 && after.Syncs - before.Syncs == profile.Callers * 2, "Concurrent Sync boundaries");
            fixture.CheckIdle();
            Check(await fixture.ReadAsync(0, false) == 1, "Post-profile probe"); fixture.CheckIdle();
            Console.WriteLine($"PASS TCP workers {profile.Name} {driver}");
            async Task WorkAsync(int worker)
            {
                bool slow = profile.Mixed && worker % 8 == 0;
                long expected = (slow ? QueryScenario.Slow : QueryScenario.One).Expected(worker);
                for (int i = 0; i < 2; i++) Check(await fixture.ReadAsync(worker, slow) == expected, "Worker response identity");
            }
        }
        Console.WriteLine("PASS TCP comparison: full wire bytes, lengths/OIDs, segmented headers/payload, fields/rows, NULL, repeated commands, SQL error recovery, early drain, 16-result single-Sync batches, peer fault, all worker profiles.");
    }
    private static void VerifyProtocol()
    {
        foreach (var scenario in QueryScenario.Readers.Append(QueryScenario.NullValue).Append(QueryScenario.NonQuery))
        {
            var queries = new QueryCatalog([scenario], 2); var catalog = new TcpQueryCatalog(queries);
            byte[] bytes = new byte[QueryPacket.GetByteCount(scenario.Sql, queries.Inputs[0][1]) + 5];
            int size = QueryPacket.Write(scenario.Sql, queries.Inputs[0][1], bytes);
            FrontendMessage.Sync().Write(bytes.AsSpan(size));
            var input = new ReadOnlySequence<byte>(bytes); var responses = new ArrayBufferWriter<byte>();
            var protocol = new TcpQueryProtocol(catalog, verifyPayloads: true);
            while (FrontendFrameParser.TryRead(ref input, out byte tag, out _, out var frame))
            {
                // Split each header and payload deterministically, independent of TCP coalescing.
                for (int split = 1; split < Math.Min(frame.Length, 12); split++)
                {
                    var a = new Segment(frame.Slice(0, split).ToArray()); var b = a.Append(frame.Slice(split).ToArray());
                    var segmented = new ReadOnlySequence<byte>(a, 0, b, b.Memory.Length);
                    Check(FrontendFrameParser.TryRead(ref segmented, out byte actual, out var payload, out _) && actual == tag && segmented.IsEmpty, "Segmented frame");
                    Check(payload.Length == frame.Length - 5, "Frame length prefix");
                }
                var whole = frame;
                Check(FrontendFrameParser.TryRead(ref whole, out _, out var body, out _), "Frame parse");
                responses.Write(protocol.Process(tag, body).Span);
            }
            Check(input.IsEmpty && protocol.Queries == 1 && protocol.Syncs == 1, "Protocol boundaries");
            Check(responses.WrittenSpan.SequenceEqual([.. queries.Replies[0][1], .. QueryWire.Ready]), "Complete backend transcript");
        }
    }
    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        internal Segment(byte[] bytes) => Memory = bytes;
        internal Segment Append(byte[] bytes)
        {
            var next = new Segment(bytes) {RunningIndex = RunningIndex + Memory.Length}; Next = next; return next;
        }
    }
    private static async Task CheckReader(MpgsqlResultReader reader, QueryScenario scenario, byte[] buffer)
    {
        Check(reader.QueryIndex == 0 && reader.Columns.Length == (scenario.NoData ? 0 : scenario.Columns), "Mpgsql description/index");
        for (int i = 0; i < reader.Columns.Length; i++)
            Check(reader.Columns.Span[i].DataTypeOid == (scenario.ByteaBytes != 0 && i == 1 ? 17U : 20U)
                && reader.Columns.Span[i].Format == FormatCode.Binary, "Mpgsql OID/format");
        int row = 0; long sum = 0;
        while (await reader.ReadAsync())
        {
            for (int column = 0; column < (scenario.ByteaBytes == 0 ? scenario.Columns : 1); column++)
                Check(reader.GetInt64(column) == (scenario.Null ? null : 1L + (long)row * (scenario.ByteaBytes == 0 ? scenario.Columns : 1) + column), "Mpgsql value/NULL");
            sum += TcpQueryOperations.Row(reader, scenario, buffer);
            if (scenario.ByteaBytes != 0) Check(buffer.AsSpan().SequenceEqual(scenario.Blob), "Mpgsql full bytea"); row++;
        }
        Check(row == scenario.Rows && sum == scenario.Expected(0) && !await reader.NextResultAsync(), "Mpgsql rows/final result");
    }
    private static async Task CheckReader(NpgsqlDataReader reader, QueryScenario scenario, byte[] buffer)
    {
        Check(reader.FieldCount == (scenario.NoData ? 0 : scenario.Columns), "Npgsql description");
        for (int i = 0; i < reader.FieldCount; i++)
            Check(reader.GetDataTypeName(i) == (scenario.ByteaBytes != 0 && i == 1 ? "bytea" : "bigint"), "Npgsql type");
        int row = 0; long sum = 0;
        while (await reader.ReadAsync())
        {
            for (int column = 0; column < (scenario.ByteaBytes == 0 ? scenario.Columns : 1); column++)
                Check(scenario.Null ? reader.IsDBNull(column) : reader.GetInt64(column) == 1L + (long)row * (scenario.ByteaBytes == 0 ? scenario.Columns : 1) + column, "Npgsql value/NULL");
            sum += TcpQueryOperations.Row(reader, scenario, buffer);
            if (scenario.ByteaBytes != 0) Check(buffer.AsSpan().SequenceEqual(scenario.Blob), "Npgsql full bytea"); row++;
        }
        Check(row == scenario.Rows && sum == scenario.Expected(0) && !await reader.NextResultAsync(), "Npgsql rows/final result");
    }
    private static async Task VerifyBatchAsync()
    {
        var catalog = new QueryCatalog([QueryScenario.One], 16);
        await using (var m = await TcpMpgsqlFixture.CreateAsync(catalog))
        {
            var before = m.Peer.Counters();
            await using var batch = m.Transports[0].Session.CreateBatch();
            var tasks = new Task[17];
            for (int i = 0; i < 16; i++) tasks[i] = batch.SendQueryAsync(QueryScenario.One.Sql, catalog.Inputs[0][i]).AsTask();
            tasks[16] = batch.SendSyncAsync().AsTask(); await Task.WhenAll(tasks);
            await using var reader = await batch.ReadResultsAsync();
            for (int i = 0; i < 16; i++)
            {
                Check(reader.QueryIndex == i && await reader.ReadAsync() && reader.GetInt64(0) == i + 1 && !await reader.ReadAsync(), "Mpgsql batch QueryIndex/value");
                Check(await reader.NextResultAsync() == (i < 15), "Mpgsql batch result boundary");
            }
            await batch.Completion;
            var after = m.Peer.Counters();
            Check(after.Queries - before.Queries == 16 && after.Syncs - before.Syncs == 1 && batch.TransactionStatus == TransactionStatus.Idle, "Mpgsql shared Sync/Ready");
        }
        var b = new TcpBatchComparisonBenchmarks();
        try
        {
            await b.SetupNpgsql(); var before = b.Peer.Counters();
            await using (var reader = await b.NativeBatch.ExecuteReaderAsync())
                for (int i = 0; i < 16; i++)
                {
                    Check(await reader.ReadAsync() && reader.GetInt64(0) == i + 1 && !await reader.ReadAsync(), "Npgsql batch ordinal/value");
                    Check(await reader.NextResultAsync() == (i < 15), "Npgsql batch result boundary");
                }
            var after = b.Peer.Counters();
            Check(after.Queries - before.Queries == 16 && after.Syncs - before.Syncs == 1, "Native Npgsql shared Sync");
            Check(await b.NpgsqlBatch() == 136, "Native Npgsql batch reuse");
        }
        finally { await b.Cleanup(); }
    }
    private static async Task VerifyFacadeBatchAsync()
    {
        foreach (bool native in new[] { false, true })
        {
            var b = new TcpFacadeBatchComparisonBenchmarks();
            try
            {
                if (native) { await b.SetupNpgsql(); b.PrepareNpgsql(); }
                else { await b.SetupMpgsql(); b.PrepareMpgsql(); }
                var before = b.Peer.Counters();
                if (native)
                {
                    await using var reader = await b.NpgsqlBatches[0].ExecuteReaderAsync();
                    for (int i = 0; i < TcpFacadeBatchComparisonBenchmarks.QueriesPerGroup; i++)
                    {
                        Check(await reader.ReadAsync() && reader.GetInt64(0) == i + 1 && !await reader.ReadAsync(), "Fresh native batch value/row count");
                        Check(await reader.NextResultAsync() == (i < 15), "Fresh native batch result ordinal");
                    }
                }
                else
                {
                    await using var reader = await b.MpgsqlBatches[0].ExecuteReaderAsync();
                    for (int i = 0; i < TcpFacadeBatchComparisonBenchmarks.QueriesPerGroup; i++)
                    {
                        Check(reader.QueryIndex == i && await reader.ReadAsync() && reader.GetInt64(0) == i + 1 && !await reader.ReadAsync(), "Facade batch QueryIndex/value/row count");
                        Check(await reader.NextResultAsync() == (i < 15), "Facade batch result boundary");
                    }
                }
                var after = b.Peer.Counters();
                Check(after.Queries - before.Queries == 16 && after.Syncs - before.Syncs == 1, "Facade/fresh native one-Sync boundary");
            }
            finally { await b.Cleanup(); }
            // Exercise the exact timed method with fresh one-shot groups, outside measurement.
            b = new();
            try
            {
                if (native) { await b.SetupNpgsql(); b.PrepareNpgsql(); }
                else { await b.SetupMpgsql(); b.PrepareMpgsql(); }
                var before = b.Peer.Counters();
                Check(await (native ? b.NpgsqlFreshBatch() : b.MpgsqlFacadeBatch()) == 136L * TcpFacadeBatchComparisonBenchmarks.GroupsPerIteration,
                    "Facade/fresh native complete wave checksum");
                var after = b.Peer.Counters();
                Check(after.Queries - before.Queries == 16 * TcpFacadeBatchComparisonBenchmarks.GroupsPerIteration
                    && after.Syncs - before.Syncs == TcpFacadeBatchComparisonBenchmarks.GroupsPerIteration,
                    "Facade/fresh native wave boundaries");
            }
            finally { await b.Cleanup(); }
        }
        Console.WriteLine("PASS explicit facade/fresh native Batch: QueryIndex/ordinals, values/row counts, one Sync per group, complete 32-group wave and disposal.");
    }

    private static async Task VerifyPeerFailureAsync()
    {
        var peer = new TcpQueryPeer(new(new QueryCatalog([QueryScenario.One], 1)));
        bool failure = false;
        try
        {
            using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, peer.Port);
            var startup = new byte[FrontendMessage.Startup("benchmark", "benchmark").GetByteCount()];
            FrontendMessage.Startup("benchmark", "benchmark").Write(startup);
            await client.GetStream().WriteAsync(startup); await client.GetStream().WriteAsync(QueryWire.Frame('?', []));
            for (int i = 0; i < 100; i++)
            {
                try { peer.CheckHealthy(); } catch (InvalidOperationException) { failure = true; break; }
                await Task.Delay(10);
            }
            Check(failure, "Peer error propagation");
        }
        finally
        {
            try { await peer.DisposeAsync(); } catch (InvalidOperationException) when (failure) { }
        }
    }
    internal static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("TCP comparison verification: " + message);
    }
}
