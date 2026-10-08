using System.Buffers;
using System.Buffers.Binary;
using Mpgsql.Internal;
using Mpgsql.Protocol;

namespace Mpgsql.Benchmarks.Queries;

internal static class QueryBenchmarkVerification
{
    internal static async Task RunAsync()
    {
        VerifyWireVectors();
        VerifyFraming();
        QueryPacketBenchmarks.VerifyNullAndEmpty();
        foreach (var cancellable in new[] {false, true})
        {
            var budget = new RowBufferBudgetBenchmarks {Cancellable = cancellable};
            try { await budget.Setup().ConfigureAwait(false); }
            finally { await budget.Cleanup().ConfigureAwait(false); }
        }
        Check(QueryLoadRunner.Rank([1, 2, 3, 4], .50) == 2 && QueryLoadRunner.Rank([1, 2, 3, 4], .99) == 4,
            "Nearest-rank percentiles");
        foreach (var name in new[] {"NoParameters", "Bigint1", "Bigint16", "Sql4096", "Text64KiB", "Jsonb64KiB", "Nullable4096", "Nullable65536"})
            new QueryPacketBenchmarks {Case = name}.Setup();
        foreach (var scenario in QueryScenario.Readers)
        foreach (var chunk in new[] {0, 1, 4096})
            await VerifyReaderAsync(scenario, chunk).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        await VerifyBatchAsync().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        foreach (var name in new[] {"ScalarEmpty", "ScalarNull", "ScalarOne", "ScalarRows128", "NonQuery", "ReturningRows128", "EarlyDispose4096"})
        {
            var benchmark = new DataSourceConsumptionBenchmarks {Case = name};
            try { await benchmark.Setup().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); }
            finally { await benchmark.Cleanup().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); }
        }
        await VerifyRecoveryAsync().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        await VerifySkippedQueryAsync().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        await VerifyPeerFailureAsync().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        await VerifyBackpressureAsync().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        foreach (var profile in QueryLoadProfile.All)
            await VerifyWorkersAsync(profile).WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        Console.WriteLine("PASS query benchmarks: complete wire vectors, header/payload fragmentation, NULL/empty, reader APIs, batch indices, recovery, backpressure, all worker profiles.");
    }

    private static void VerifyWireVectors()
    {
        Check(QueryWire.Ready.AsSpan().SequenceEqual(new byte[] {(byte)'Z', 0, 0, 0, 5, (byte)'I'}), "ReadyForQuery bytes");
        byte[] expected =
        [
            (byte)'1', 0, 0, 0, 4, (byte)'2', 0, 0, 0, 4,
            (byte)'T', 0, 0, 0, 27, 0, 1, (byte)'c', (byte)'0', 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 20, 0, 8, 255, 255, 255, 255, 0, 1,
            (byte)'D', 0, 0, 0, 18, 0, 1, 0, 0, 0, 8, 0, 0, 0, 0, 0, 0, 0, 1,
            (byte)'C', 0, 0, 0, 13, (byte)'S', (byte)'E', (byte)'L', (byte)'E', (byte)'C', (byte)'T', (byte)' ', (byte)'1', 0
        ];
        Check(QueryWire.Reply(QueryScenario.One, 0).AsSpan().SequenceEqual(expected), "Complete bigint transcript bytes");
        var nullReply = new ReadOnlySequence<byte>(QueryWire.Reply(QueryScenario.NullValue, 0));
        var nullFound = false;
        while (BackendMessageReader.TryRead(ref nullReply, out var message))
        {
            if (message.Kind == BackendMessageKind.DataRow)
            {
                nullFound = true;
                Check(message.Payload.Length == 6 && message.Payload.Slice(2).ToArray().AsSpan().SequenceEqual(new byte[] {255, 255, 255, 255}), "SQL NULL field length");
            }
        }
        Check(nullFound && nullReply.IsEmpty, "NULL transcript consumed");
    }

    private static void VerifyFraming()
    {
        var packet = QueryWire.Frame('S', []);
        for (var split = 0; split <= packet.Length; split++)
        {
            var partial = new ReadOnlySequence<byte>(packet.AsMemory(0, split));
            var result = FrontendFrameParser.TryRead(ref partial, out _, out _, out _);
            Check(result == (split == packet.Length) && (result || partial.Length == split), "Incomplete frame unchanged");
            var segmented = Segment(packet, split);
            Check(FrontendFrameParser.TryRead(ref segmented, out var tag, out var body, out var frame)
                  && tag == (byte)'S' && body.IsEmpty && frame.Length == 5 && segmented.IsEmpty, "Segmented header");
        }
        var bind = QueryWire.Frame('B', new byte[37]);
        for (var split = 1; split < bind.Length; split++)
        {
            var segmented = Segment(bind, split);
            Check(FrontendFrameParser.TryRead(ref segmented, out _, out var body, out _) && body.Length == 37, "Segmented payload");
        }
        foreach (var length in new[] {3, -1, 64 * 1024 * 1024 + 1})
        {
            byte[] invalid = [.. packet];
            BinaryPrimitives.WriteInt32BigEndian(invalid.AsSpan(1), length);
            try
            {
                var input = new ReadOnlySequence<byte>(invalid);
                FrontendFrameParser.TryRead(ref input, out _, out _, out _);
                throw new InvalidOperationException("Invalid frame length accepted.");
            }
            catch (InvalidDataException) { }
        }
    }

    private static async Task VerifyReaderAsync(QueryScenario scenario, int chunk)
    {
        // One-byte delivery is needed for headers; large row transcripts use 4096-byte chunks.
        if (chunk == 1 && scenario.Rows > 1)
        {
            return;
        }
        if (chunk == 1 && scenario.ByteaBytes > 0)
        {
            return;
        }
        var catalog = new QueryCatalog([scenario], 2);
        await using var peer = new QueryPeer(catalog, chunk, true);
        var checksum = await ReadRawChecked(peer, catalog, scenario, 1).ConfigureAwait(false);
        Check(checksum == scenario.Expected(1), "Raw reader checksum");
        var expected = new byte[QueryPacket.GetByteCount(scenario.Sql, catalog.Inputs[0][1]) + 5];
        var size = QueryPacket.Write(scenario.Sql, catalog.Inputs[0][1], expected);
        FrontendMessage.Sync().Write(expected.AsSpan(size));
        Check(peer.Captured().AsSpan().SequenceEqual(expected), "Complete outgoing P/B/D/E/Sync bytes");
        peer.ThrowIfFailed();
        Check(peer.Session.IsHealthy && peer.Queries == 1 && peer.Syncs == 1, "Raw boundary");
        await QueryOperations.RawAsync(peer, catalog, scenario).ConfigureAwait(false);

        await using var fixture = await QuerySourceFixture.CreateAsync(catalog, chunk: chunk).ConfigureAwait(false);
        Check(await QueryOperations.DataSourceAsync(fixture, scenario, 1).ConfigureAwait(false) == scenario.Expected(1), "DataSource reader checksum");
        fixture.CheckIdle();
        Check(await QueryOperations.DataSourceAsync(fixture, scenario).ConfigureAwait(false) == scenario.Expected(0), "Following source request");
        fixture.CheckIdle();
    }

    private static async Task<long> ReadRawChecked(QueryPeer peer, QueryCatalog catalog,
        QueryScenario scenario, int worker)
    {
        await using var batch = peer.Session.CreateBatch();
        var send = batch.SendQueryAsync(scenario.Sql, catalog.Inputs[0][worker]);
        var sync = batch.SendSyncAsync();
        await Task.WhenAll(send.AsTask(), sync.AsTask()).ConfigureAwait(false);
        await using var reader = await batch.ReadResultsAsync().ConfigureAwait(false);
        Check(reader.QueryIndex == 0 && reader.Columns.Length == scenario.Columns, "Description/index");
        foreach (var field in reader.Columns.Span)
            Check(field.Format == FormatCode.Binary && field.DataTypeOid is 17 or 20, "OID/format");
        var rows = 0;
        long checksum = 0;
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            rows++;
            checksum += QueryOperations.Row(reader, scenario);
        }
        Check(rows == scenario.Rows && reader.CommandTag == $"SELECT {scenario.Rows}", "Rows/CommandComplete");
        Check(!await reader.NextResultAsync().ConfigureAwait(false), "Final result");
        await batch.Completion.ConfigureAwait(false);
        Check(batch.TransactionStatus == TransactionStatus.Idle, "ReadyForQuery status");
        return checksum;
    }

    private static async Task VerifyBatchAsync()
    {
        var catalog = new QueryCatalog([QueryScenario.One], 16);
        await using var peer = new QueryPeer(catalog);
        await using var batch = peer.Session.CreateBatch();
        var sends = new Task[17];
        for (var i = 0; i < 16; i++)
        {
            sends[i] = batch.SendQueryAsync(QueryScenario.One.Sql, catalog.Inputs[0][i]).AsTask();
        }
        sends[16] = batch.SendSyncAsync().AsTask();
        await Task.WhenAll(sends).ConfigureAwait(false);
        await using var reader = await batch.ReadResultsAsync().ConfigureAwait(false);
        for (var i = 0; i < 16; i++)
        {
            Check(reader.QueryIndex == i && await reader.ReadAsync().ConfigureAwait(false) && reader.GetInt64(0) == i + 1, "Batch index/value");
            Check(!await reader.ReadAsync().ConfigureAwait(false), "One row per batch result");
            Check(await reader.NextResultAsync().ConfigureAwait(false) == i < 15, "Batch result boundary");
        }
        await batch.Completion.ConfigureAwait(false);
        Check(peer.Queries == 16 && peer.Syncs == 1 && batch.TransactionStatus == TransactionStatus.Idle, "Shared Sync");
    }

    private static async Task VerifyRecoveryAsync()
    {
        var catalog = new QueryCatalog([QueryScenario.One, QueryScenario.Failing], 1);
        await using var fixture = await QuerySourceFixture.CreateAsync(catalog).ConfigureAwait(false);
        try
        {
            await QueryOperations.DataSourceAsync(fixture, QueryScenario.Failing).ConfigureAwait(false);
            throw new InvalidOperationException("Expected synthetic SQL error.");
        }
        catch (MpgsqlServerException error) { Check(error.SqlState == "22012", "SQLSTATE"); }
        fixture.CheckIdle();
        Check(await QueryOperations.DataSourceAsync(fixture, QueryScenario.One).ConfigureAwait(false) == 1, "Recovery after error");
        fixture.CheckIdle();
    }

    private static async Task VerifyWorkersAsync(QueryLoadProfile profile)
    {
        await using var fixture = await QuerySourceFixture.CreateAsync(profile.CreateCatalog(), profile.Connections,
            profile.InFlight, profile.RowBudget).ConfigureAwait(false);
        var pending = new Task<QueryOperations.Timing>[profile.Callers];
        for (var worker = 0; worker < pending.Length; worker++)
        {
            var slow = profile.Mixed && worker % 8 == 0;
            pending[worker] = QueryOperations.TimedAsync(fixture, slow ? QueryScenario.Slow : QueryScenario.One,
                slow ? 1 : 0, worker, slow);
        }
        var results = await Task.WhenAll(pending).ConfigureAwait(false);
        for (var worker = 0; worker < results.Length; worker++)
        {
            Check(results[worker].Checksum == (profile.Mixed && worker % 8 == 0 ? QueryScenario.Slow : QueryScenario.One).Expected(worker), "Worker response association");
        }
        fixture.CheckIdle();
        if (profile.Mixed)
        {
            Check(fixture.MaxObservedBufferedRowBytes > 0, "Backpressure observed");
        }
        Check(fixture.Peers.Length == profile.Connections, "Prewarmed pool size");
        Check(await QueryOperations.DataSourceAsync(fixture, QueryScenario.One).ConfigureAwait(false) == 1, "Following worker request");
        fixture.CheckIdle();
    }

    private static async Task VerifySkippedQueryAsync()
    {
        var catalog = new QueryCatalog([QueryScenario.Failing, QueryScenario.One], 1);
        await using var peer = new QueryPeer(catalog);
        await using var batch = peer.Session.CreateBatch();
        var first = batch.SendQueryAsync(QueryScenario.Failing.Sql, catalog.Inputs[0][0]).AsTask();
        var skipped = batch.SendQueryAsync(QueryScenario.One.Sql, catalog.Inputs[1][0]).AsTask();
        var sync = batch.SendSyncAsync().AsTask();
        await Task.WhenAll(first, skipped, sync).ConfigureAwait(false);
        try
        {
            await using var reader = await batch.ReadResultsAsync().ConfigureAwait(false);
            while (await reader.NextResultAsync().ConfigureAwait(false)) { }
            throw new InvalidOperationException("Expected error before skipped query.");
        }
        catch (MpgsqlServerException error) { Check(error.SqlState == "22012" && error.QueryIndex == 0, "Skipped response/error index"); }
        Check(await QueryOperations.RawAsync(peer, catalog, QueryScenario.One).ConfigureAwait(false) == 1, "Following raw group recovers");
        Check(peer.Queries == 2 && peer.Syncs == 2, "Skipped Execute not confirmed");
    }

    private static async Task VerifyPeerFailureAsync()
    {
        var peer = new QueryPeer(new QueryCatalog([QueryScenario.One], 1));
        var observed = false;
        try
        {
            await peer.ClientWriter.WriteAsync(QueryWire.Frame('X', [])).ConfigureAwait(false);
            try { await peer.Session.Completion.ConfigureAwait(false); }
            catch { }
            try { peer.ThrowIfFailed(); }
            catch (InvalidOperationException error) when (error.InnerException is InvalidDataException) { observed = true; }
            Check(observed && !peer.Session.IsHealthy, "Peer errors fault the transport");
        }
        finally
        {
            try { await peer.DisposeAsync().ConfigureAwait(false); }
            catch (InvalidOperationException error) when (observed && error.InnerException is InvalidDataException) { }
        }
    }

    private static async Task VerifyBackpressureAsync()
    {
        var catalog = new QueryCatalog([QueryScenario.Slow, QueryScenario.One], 1);
        await using var fixture = await QuerySourceFixture.CreateAsync(catalog, inFlight: 8, rowBytes: 65536).ConfigureAwait(false);
        await using var reader = await fixture.Source.ExecuteReaderAsync(QueryScenario.Slow.Sql, catalog.Inputs[0][0]).ConfigureAwait(false);
        const long rowPayload = 2 + 4 + 8 + 4 + 8192;
        while (fixture.Peers[0].Session.BufferedRowBytes < rowPayload * 7)
        {
            await Task.Delay(1).ConfigureAwait(false);
        }
        fixture.ObserveBuffers();
        var neighbour = fixture.Source.ExecuteScalarAsync<long>(QueryScenario.One.Sql, catalog.Inputs[1][0]).AsTask();
        while (fixture.Peers[0].Syncs != 2)
        {
            await Task.Delay(1).ConfigureAwait(false);
        }
        Check(!neighbour.IsCompleted && fixture.Peers[0].Session.BufferedRowBytes == rowPayload * 7,
            "Row budget pauses later responses on the same transport");
        Check(await QueryOperations.ConsumeAsync(reader, QueryScenario.Slow).ConfigureAwait(false) == QueryScenario.Slow.Expected(0),
            "Backpressure row values");
        Check((await neighbour.ConfigureAwait(false)).Value == 1, "Neighbour resumes after drain");
        fixture.CheckIdle();
    }
    private static ReadOnlySequence<byte> Segment(byte[] bytes, int split)
    {
        var first = new Part(bytes.AsMemory(0, split));
        first.Initialize();
        var last = new Part(bytes.AsMemory(split));
        last.Initialize();
        first.Link(last);
        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }
    internal static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Query benchmark verification: " + message);
        }
    }

    private sealed class Part(ReadOnlyMemory<byte> memory) : ReadOnlySequenceSegment<byte>
    {
        internal void Initialize()
        {
            Memory = memory;
        }
        internal void Link(Part next)
        {
            next.RunningIndex = RunningIndex + Memory.Length;
            Next = next;
        }
    }
}