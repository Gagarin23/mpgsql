using System.Buffers;
using Mpgsql.Internal;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class LogicalCancellationTests
{
    private static readonly TimeSpan PromptTimeout = TimeSpan.FromSeconds(2);
    // A packet larger than the writer's drain limit flushes before the following Sync.
    private static readonly string SeparateSyncSql = "select 1::bigint" + new string(' ', 65536);

    private static MpgsqlDataSource Source(ScriptedSession wire, int inFlight = 2)
        => new(_ => ValueTask.FromResult(wire.Session),
            (_, _) => throw new InvalidOperationException("Shared requests must not send a server cancel."),
            new()
            {
                MaxConnections = 1, MaxInFlightPerConnection = inFlight,
                RecoveryTimeout = TimeSpan.FromMilliseconds(50)
            });

    private static async Task<string> ThroughSync(ScriptedSession wire, int count = 1)
    {
        var tags = new List<char>();
        while (tags.Count(t => t == 'S') < count) tags.AddRange(Tags(await wire.ReadOutputAsync()));
        return new string([.. tags]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task CancellationDuringBlockedFlushReleasesInputsAndPreservesNeighbour(
        bool blockedSync, bool descriptionArrived)
    {
        using var input = new BlockingInputMemory();
        await using var wire = new ScriptedSession(blockWrites: true);
        await using var source = Source(wire);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        MpgsqlParameter[] parameters = [MpgsqlParameter.Int64Array(input.Memory)];
        var sql = "select $1::bigint[]" + (blockedSync ? new string(' ', 65536) : "");
        var opening = source.ExecuteReaderAsync(sql, parameters, request.Token).AsTask();
        var held = await wire.Outgoing.Reader.ReadAsync(TestContext.Current.CancellationToken);
        var heldTags = new string(Tags(held.Buffer.ToArray()));
        Assert.Contains(heldTags, new[] { "PBDE", "PBDES" });
        if (blockedSync)
        {
            Assert.Equal("PBDE", heldTags);
            wire.Outgoing.Reader.AdvanceTo(held.Buffer.End);
            held = await wire.Outgoing.Reader.ReadAsync(TestContext.Current.CancellationToken);
            heldTags = new string(Tags(held.Buffer.ToArray()));
            Assert.Equal("S", heldTags);
        }
        var neighbour = source.ExecuteScalarAsync<long>("select 8::bigint",
            cancellationToken: TestContext.Current.CancellationToken).AsTask();
        if (descriptionArrived) await wire.WriteAsync(Begin(20));

        request.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(PromptTimeout,
            TestContext.Current.CancellationToken));
        Assert.Equal(1, input.Reads);
        input.Revoke();
        parameters[0] = default;
        Assert.False(neighbour.IsCompleted);
        Assert.False(wire.Session.Completion.IsCompleted);

        wire.Outgoing.Reader.AdvanceTo(held.Buffer.End);
        bool syncPublished = heldTags.EndsWith('S');
        Assert.Equal(syncPublished ? "PBDES" : "SPBDES", await ThroughSync(wire, syncPublished ? 1 : 2));
        await wire.WriteAsync(descriptionArrived
            ? Join(Row(Int64(1)), Command(), Ready(), Query(8), Ready())
            : Join(Query(1), Ready(), Query(8), Ready()));
        Assert.Equal(8, (await neighbour.WaitAsync(TestTimeout, TestContext.Current.CancellationToken)).Value);
        Assert.Equal(1, input.Reads);
        Assert.True(wire.Session.IsHealthy);
    }

    [Fact]
    public async Task CancellationBeforeEncodingDropsBorrowedInputsBehindNeighbourFlush()
    {
        using var input = new BlockingInputMemory();
        await using var wire = new ScriptedSession(blockWrites: true);
        await using var source = Source(wire);
        var neighbour = source.ExecuteScalarAsync<long>("select 7::bigint",
            cancellationToken: TestContext.Current.CancellationToken).AsTask();
        var held = await wire.Outgoing.Reader.ReadAsync(TestContext.Current.CancellationToken);
        var heldTags = new string(Tags(held.Buffer.ToArray()));
        Assert.Contains(heldTags, new[] { "PBDE", "PBDES" });
        using var request = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        MpgsqlParameter[] parameters = [MpgsqlParameter.Int64Array(input.Memory)];
        var opening = source.ExecuteReaderAsync("select $1::bigint[]", parameters, request.Token).AsTask();
        request.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(PromptTimeout,
            TestContext.Current.CancellationToken));
        Assert.Equal(0, input.Reads);
        input.Revoke();
        parameters[0] = default;
        wire.Outgoing.Reader.AdvanceTo(held.Buffer.End);
        Assert.Equal(heldTags.EndsWith('S') ? "S" : "SS", await ThroughSync(wire, heldTags.EndsWith('S') ? 1 : 2));
        await wire.WriteAsync(Join(Query(7), Ready(), Ready()));
        Assert.Equal(7, (await neighbour.WaitAsync(TestTimeout, TestContext.Current.CancellationToken)).Value);
        Assert.Equal(0, input.Reads);
        Assert.True(wire.Session.IsHealthy);
    }

    [Fact]
    public async Task CancellationDuringEncodingWaitsForBorrowedInputsButNotSyncFlush()
    {
        using var input = new BlockingInputMemory(blockEncoding: true);
        await using var wire = new ScriptedSession(blockWrites: true);
        await using var source = Source(wire);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var opening = source.ExecuteReaderAsync("select $1::bigint[]",
            new[] { MpgsqlParameter.Int64Array(input.Memory) }, request.Token).AsTask();
        await input.Entered.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        var neighbour = source.ExecuteScalarAsync<long>("select 8::bigint",
            cancellationToken: TestContext.Current.CancellationToken).AsTask();
        try
        {
            request.Cancel();
            var delay = Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.Same(delay, await Task.WhenAny(opening, delay));
            Assert.False(wire.HasOutput()); // no partial query was advanced
        }
        finally { input.Resume(); }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(PromptTimeout,
            TestContext.Current.CancellationToken));
        input.Revoke();
        var sync = await wire.Outgoing.Reader.ReadAsync(TestContext.Current.CancellationToken);
        var tags = new string(Tags(sync.Buffer.ToArray()));
        Assert.StartsWith("S", tags);
        Assert.False(neighbour.IsCompleted);
        wire.Outgoing.Reader.AdvanceTo(sync.Buffer.End);
        int remainingSync = 2 - tags.Count(t => t == 'S');
        if (remainingSync != 0) tags += await ThroughSync(wire, remainingSync);
        Assert.Equal("SPBDES", tags);
        await wire.WriteAsync(Join(Ready(), Query(8), Ready()));
        Assert.Equal(8, (await neighbour.WaitAsync(TestTimeout, TestContext.Current.CancellationToken)).Value);
        Assert.Equal(1, input.Reads);
        Assert.True(wire.Session.IsHealthy);
    }

    [Fact]
    public async Task CancelledRequestRetainsSchedulingSlotUntilReadyForQuery()
    {
        await using var wire = new ScriptedSession(blockWrites: true);
        await using var source = Source(wire, inFlight: 1);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var opening = source.ExecuteReaderAsync("select 1::bigint", cancellationToken: request.Token).AsTask();
        var held = await wire.Outgoing.Reader.ReadAsync(TestContext.Current.CancellationToken);
        var heldTags = new string(Tags(held.Buffer.ToArray()));
        Assert.Contains(heldTags, new[] { "PBDE", "PBDES" });
        request.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(PromptTimeout,
            TestContext.Current.CancellationToken));

        var neighbour = source.ExecuteScalarAsync<long>("select 8::bigint",
            cancellationToken: TestContext.Current.CancellationToken).AsTask();
        await Task.Delay(100, TestContext.Current.CancellationToken); // shared recovery has no exclusive deadline
        Assert.False(neighbour.IsCompleted);
        Assert.True(wire.Session.IsHealthy);
        wire.Outgoing.Reader.AdvanceTo(held.Buffer.End);
        if (!heldTags.EndsWith('S')) Assert.Equal("S", await ThroughSync(wire));
        await wire.WriteAsync(Query(1));
        Assert.False(neighbour.IsCompleted);
        Assert.False(wire.HasOutput());
        await wire.WriteAsync(Ready());
        Assert.Equal("PBDES", await ThroughSync(wire));
        await wire.WriteAsync(Join(Query(8), Ready()));
        Assert.Equal(8, (await neighbour.WaitAsync(TestTimeout, TestContext.Current.CancellationToken)).Value);
    }

    [Fact]
    public async Task ReadyForQueryDoesNotReleaseSlotBeforeBlockedSyncFlushCompletes()
    {
        await using var wire = new ScriptedSession(blockWrites: true);
        await using var source = Source(wire, inFlight: 1);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var opening = source.ExecuteReaderAsync(SeparateSyncSql, cancellationToken: request.Token).AsTask();
        Assert.Equal("PBDE", new string(Tags(await wire.ReadOutputAsync())));
        var sync = await wire.Outgoing.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("S", new string(Tags(sync.Buffer.ToArray())));
        request.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(PromptTimeout,
            TestContext.Current.CancellationToken));

        var neighbour = source.ExecuteScalarAsync<long>("select 8::bigint",
            cancellationToken: TestContext.Current.CancellationToken).AsTask();
        await wire.WriteAsync(Join(Query(1), Ready()));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(neighbour.IsCompleted);
        Assert.True(wire.Session.IsHealthy);
        wire.Outgoing.Reader.AdvanceTo(sync.Buffer.End);
        Assert.Equal("PBDES", await ThroughSync(wire));
        await wire.WriteAsync(Join(Query(8), Ready()));
        Assert.Equal(8, (await neighbour.WaitAsync(TestTimeout, TestContext.Current.CancellationToken)).Value);
    }

    [Fact]
    public async Task ReaderCancellationAndDisposalDoNotWaitForBlockedSyncFlush()
    {
        await using var wire = new ScriptedSession(blockWrites: true);
        await using var source = Source(wire, inFlight: 1);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var opening = source.ExecuteReaderAsync(SeparateSyncSql, cancellationToken: request.Token).AsTask();
        Assert.Equal("PBDE", new string(Tags(await wire.ReadOutputAsync())));
        var sync = await wire.Outgoing.Reader.ReadAsync(TestContext.Current.CancellationToken);
        await wire.WriteAsync(Begin(20));
        await using var reader = await opening.WaitAsync(PromptTimeout, TestContext.Current.CancellationToken);
        var reading = reader.ReadAsync().AsTask();
        request.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading.WaitAsync(PromptTimeout,
            TestContext.Current.CancellationToken));
        await reader.DisposeAsync().AsTask().WaitAsync(PromptTimeout, TestContext.Current.CancellationToken);

        var neighbour = source.ExecuteScalarAsync<long>("select 8::bigint",
            cancellationToken: TestContext.Current.CancellationToken).AsTask();
        Assert.False(neighbour.IsCompleted);
        wire.Outgoing.Reader.AdvanceTo(sync.Buffer.End);
        await wire.WriteAsync(Join(Row(Int64(1)), Command(), Ready()));
        Assert.Equal("PBDES", await ThroughSync(wire));
        await wire.WriteAsync(Join(Query(8), Ready()));
        Assert.Equal(8, (await neighbour.WaitAsync(TestTimeout, TestContext.Current.CancellationToken)).Value);
        Assert.True(wire.Session.IsHealthy);
    }

    [Fact]
    public async Task CancellationBeforeAtomicAdmissionReleasesInputsWithoutSync()
    {
        using var input = new BlockingInputMemory();
        await using var wire = new ScriptedSession();
        await using var source = Source(wire);
        var pooled = new PooledSession(wire.Session) { Active = 1 };
        using var request = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        request.Cancel();
        var execution = new QueryExecution(source, pooled, null,
            [new("select $1::bigint[]", new[] { MpgsqlParameter.Int64Array(input.Memory) })], request.Token);
        var opening = execution.OpenReaderAsync(waitForInputRelease: true).AsTask();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(PromptTimeout,
            TestContext.Current.CancellationToken));
        input.Revoke();
        await execution.FinishAsync(discard: true).AsTask().WaitAsync(TestTimeout,
            TestContext.Current.CancellationToken);
        Assert.Equal(0, input.Reads);
        Assert.Equal(0, pooled.Active);
        Assert.False(wire.HasOutput());
        Assert.True(wire.Session.IsHealthy);
    }
}
