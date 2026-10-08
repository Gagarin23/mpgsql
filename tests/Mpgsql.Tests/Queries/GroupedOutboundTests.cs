using System.Buffers;
using System.IO.Pipelines;
using Mpgsql.Internal;
using Mpgsql.Protocol;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class GroupedOutboundTests
{
    private const string Sql = "select $1::bigint";
    private static readonly string LargeSql = "select $1::bigint[]" + new string(' ', 65536);

    private static async Task<byte[]> ThroughSync(ScriptedSession wire, int count = 1)
    {
        var bytes = new List<byte>();
        while (Tags([.. bytes]).Count(tag => tag == 'S') < count)
            bytes.AddRange(await wire.ReadOutputAsync());
        return [.. bytes];
    }

    private static void ExpectedQuery(ArrayBufferWriter<byte> destination, string sql, long value)
    {
        FrontendMessage.Parse(sql, parameterTypes: new uint[] { 20 }).Write(destination);
        FrontendMessage.Bind(parameters: new ReadOnlyMemory<byte>?[] { Int64(value) },
            parameterFormats: new[] { FormatCode.Binary }, resultFormats: new[] { FormatCode.Binary }).Write(destination);
        FrontendMessage.Describe(StatementOrPortal.Portal).Write(destination);
        FrontendMessage.Execute().Write(destination);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(257)]
    public async Task CompleteWireAndResponseIndicesKeepTheGroupAheadOfItsSyncAndNeighbour(int count)
    {
        await using var wire = new ScriptedSession(blockWrites: true);
        await using var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        var queries = new QueryDefinition[count];
        var expected = new ArrayBufferWriter<byte>();
        for (int i = 0; i < count; i++)
        {
            queries[i] = new(Sql, new[] { MpgsqlParameter.Int64(i) });
            ExpectedQuery(expected, Sql, i);
        }
        FrontendMessage.Sync().Write(expected);
        Task send = batch.SendQueriesAsync(queries);
        Task sync = batch.SendSyncAsync().AsTask();
        await using var neighbour = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        Task nextSend = neighbour.SendQueryAsync(Sql, new[] { MpgsqlParameter.Int64(999) }).AsTask();
        Task nextSync = neighbour.SendSyncAsync().AsTask();
        ExpectedQuery(expected, Sql, 999);
        FrontendMessage.Sync().Write(expected);
        Assert.Equal(expected.WrittenSpan.ToArray(), await ThroughSync(wire, 2));
        await Task.WhenAll(send, sync, nextSend, nextSync).WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        var responses = new byte[count + 3][];
        for (int i = 0; i < count; i++) responses[i] = Query(i);
        responses[count] = Ready();
        responses[count + 1] = Query(999);
        responses[count + 2] = Ready();
        await wire.WriteAsync(Join(responses), fragment: 7);
        await using var reader = await batch.ReadResultsAsync();
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(i, reader.QueryIndex);
            Assert.True(await reader.ReadAsync());
            Assert.Equal(i, reader.GetInt64(0));
            Assert.False(await reader.ReadAsync());
            Assert.Equal(i + 1 < count, await reader.NextResultAsync());
        }
        await batch.Completion;
        await using var nextReader = await neighbour.ReadResultsAsync();
        Assert.Equal(0, nextReader.QueryIndex);
        Assert.True(await nextReader.ReadAsync());
        Assert.Equal(999, nextReader.GetInt64(0));
        Assert.False(await nextReader.NextResultAsync());
        await neighbour.Completion;
        Assert.True(wire.Session.IsHealthy);
        Assert.Equal(0, wire.Session.BufferedRowBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationReleasesUnencodedInputsWhileTheGroupOrItsNeighbourFlushIsBlocked(bool partial)
    {
        using var first = new BlockingInputMemory();
        using var queued = new BlockingInputMemory();
        await using var wire = new ScriptedSession(blockWrites: true);
        await using var source = new MpgsqlDataSource(_ => ValueTask.FromResult(wire.Session),
            (_, _) => ValueTask.CompletedTask, new() { MaxConnections = 1, MaxInFlightPerConnection = 2 });
        await using var preceding = partial ? null : wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        Task? precedingSend = null;
        Task? precedingSync = null;
        ReadResult held = default;
        if (preceding is not null)
        {
            precedingSend = preceding.SendQueryAsync(Sql, new[] { MpgsqlParameter.Int64(7) }).AsTask();
            precedingSync = preceding.SendSyncAsync().AsTask();
            held = await wire.Outgoing.Reader.ReadAsync(TestContext.Current.CancellationToken);
        }
        using var request = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pooled = new PooledSession(wire.Session) { Active = 1 };
        var execution = new QueryExecution(source, pooled, null,
            [new(LargeSql, new[] { MpgsqlParameter.Int64Array(first.Memory) }),
             new("select $1::bigint[]", new[] { MpgsqlParameter.Int64Array(queued.Memory) })], request.Token);
        var opening = execution.OpenReaderAsync(waitForInputRelease: true).AsTask();
        if (partial) held = await wire.Outgoing.Reader.ReadAsync(TestContext.Current.CancellationToken);
        var tags = new string(Tags(held.Buffer.ToArray()));
        if (partial) Assert.Equal("PBDE", tags);
        else Assert.Contains(tags, new[] { "PBDE", "PBDES" });
        request.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken));
        Assert.Equal(partial ? 1 : 0, first.Reads);
        Assert.Equal(0, queued.Reads);
        first.Revoke();
        queued.Revoke();
        Assert.Equal(1, pooled.Active);
        wire.Outgoing.Reader.AdvanceTo(held.Buffer.End);
        int remainingSyncs = (partial ? 1 : 2) - tags.Count(tag => tag == 'S');
        Assert.Equal(new string('S', remainingSyncs), new string(Tags(await ThroughSync(wire, remainingSyncs))));
        if (precedingSend is not null) await Task.WhenAll(precedingSend, precedingSync!);
        await wire.WriteAsync(partial ? Join(Query(11), Ready()) : Join(Query(7), Ready(), Ready()));
        await execution.FinishAsync(discard: true).AsTask().WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.Equal(0, pooled.Active);
        Assert.Equal(0, queued.Reads);
        Assert.True(wire.Session.IsHealthy);
        Assert.Equal(0, wire.Session.BufferedRowBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransportFailureKeepsActiveEncoderInputsAndReleasesPausedInputs(bool partial)
    {
        using var first = new BlockingInputMemory(blockEncoding: !partial);
        using var queued = new BlockingInputMemory();
        await using var wire = new ScriptedSession(blockWrites: partial);
        await using var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        Task send = batch.SendQueriesAsync(
            [new(partial ? LargeSql : "select $1::bigint[]", new[] { MpgsqlParameter.Int64Array(first.Memory) }),
             new("select $1::bigint[]", new[] { MpgsqlParameter.Int64Array(queued.Memory) })]);
        Task sync = batch.SendSyncAsync().AsTask();
        if (partial)
        {
            var held = await wire.Outgoing.Reader.ReadAsync(TestContext.Current.CancellationToken);
            Assert.Equal("PBDE", new string(Tags(held.Buffer.ToArray())));
            wire.Outgoing.Reader.AdvanceTo(held.Buffer.Start, held.Buffer.Start);
        }
        else await first.Entered.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        try
        {
            await wire.Incoming.Writer.CompleteAsync();
            await Assert.ThrowsAnyAsync<IOException>(() => wire.Session.Completion.WaitAsync(TestTimeout,
                TestContext.Current.CancellationToken));
            if (!partial) Assert.False(send.IsCompleted);
        }
        finally { first.Resume(); }
        await Assert.ThrowsAnyAsync<IOException>(() => send.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<IOException>(() => sync.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<IOException>(() => batch.ObserveCompletionAsync().AsTask());
        first.Revoke(); queued.Revoke();
        Assert.Equal(1, first.Reads);
        Assert.Equal(0, queued.Reads);
        Assert.Equal(0, wire.Session.BufferedRowBytes);
    }
}
