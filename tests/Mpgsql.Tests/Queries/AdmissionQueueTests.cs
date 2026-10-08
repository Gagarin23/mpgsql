using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class AdmissionQueueTests
{
    private static MpgsqlDataSource Source(ScriptedSession wire)
        => new(_ => ValueTask.FromResult(wire.Session), (_, _) => ValueTask.CompletedTask,
            new() { MaxConnections = 1, MaxInFlightPerConnection = 1 });

    private static async Task<string> ThroughSync(ScriptedSession wire)
    {
        var tags = new List<char>();
        while (!tags.Contains('S')) tags.AddRange(Tags(await wire.ReadOutputAsync()));
        return new string([.. tags]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public async Task CancelledQueuedQueryNeverUsesItsInputsOrOwnsTheFollowingBoundary(int groupSize)
    {
        var token = TestContext.Current.CancellationToken;
        using var input = new BlockingInputMemory();
        await using var wire = new ScriptedSession();
        await using var source = new MpgsqlDataSource(_ => ValueTask.FromResult(wire.Session),
            (_, _) => ValueTask.CompletedTask,
            new() { MaxConnections = 1, MaxInFlightPerConnection = 1,
                SyncGroupSize = groupSize, SyncGroupTimeout = TimeSpan.FromMilliseconds(1) });
        var held = await source.OpenConnectionAsync(token);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
        var cancelled = source.ExecuteReaderAsync("select $1::bigint[]",
            new[] { MpgsqlParameter.Int64Array(input.Memory) }, request.Token).AsTask();
        var following = source.ExecuteScalarAsync<long>("select 9::bigint", cancellationToken: token).AsTask();
        Assert.False(cancelled.IsCompleted); Assert.False(following.IsCompleted);
        request.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(TestTimeout, token));
        input.Revoke();
        Assert.Equal(0, input.Reads); Assert.False(wire.HasOutput());
        await held.DisposeAsync();
        Assert.Equal("PBDES", await ThroughSync(wire));
        await wire.WriteAsync(Join(Query(9), Ready()), fragment: 1);
        Assert.Equal(9, (await following.WaitAsync(TestTimeout, token)).Value);
        Assert.Equal(0, input.Reads);
        Assert.True(wire.Session.IsIdleAndHealthy);
        Assert.Equal(0, wire.Session.BufferedRowBytes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public async Task QueuedQueriesAndExclusiveConnectionsKeepTheSameFifoAndReaderLifetime(int groupSize)
    {
        var token = TestContext.Current.CancellationToken;
        await using var wire = new ScriptedSession();
        await using var source = new MpgsqlDataSource(_ => ValueTask.FromResult(wire.Session),
            (_, _) => ValueTask.CompletedTask,
            new() { MaxConnections = 1, MaxInFlightPerConnection = 1,
                SyncGroupSize = groupSize, SyncGroupTimeout = TimeSpan.FromMilliseconds(1) });
        var held = await source.OpenConnectionAsync(token);
        var first = source.ExecuteReaderAsync("select 1::bigint", cancellationToken: token).AsTask();
        var exclusive = source.OpenConnectionAsync(token).AsTask();
        var last = source.ExecuteScalarAsync<long>("select 3::bigint", cancellationToken: token).AsTask();
        await held.DisposeAsync();
        Assert.Equal("PBDES", await ThroughSync(wire));
        await wire.WriteAsync(Join(Query(1), Ready()), fragment: 1);
        await using var reader = await first.WaitAsync(TestTimeout, token);
        Assert.True(await reader.ReadAsync()); Assert.Equal(1, reader.GetInt64(0));
        Assert.False(exclusive.IsCompleted); Assert.False(last.IsCompleted); Assert.False(wire.HasOutput());
        await reader.DisposeAsync();
        var connection = await exclusive.WaitAsync(TestTimeout, token);
        Assert.False(last.IsCompleted); Assert.False(wire.HasOutput());
        await connection.DisposeAsync();
        Assert.Equal("PBDES", await ThroughSync(wire));
        await wire.WriteAsync(Join(Query(3), Ready()), fragment: 1);
        Assert.Equal(3, (await last.WaitAsync(TestTimeout, token)).Value);
        Assert.True(wire.Session.IsIdleAndHealthy);
        Assert.Equal(0, wire.Session.BufferedRowBytes);
    }

    [Fact]
    public async Task ReturnedSlotGoesToOldestWaiterBeforeANewCaller()
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire);
        var held = await source.OpenConnectionAsync(TestContext.Current.CancellationToken);
        var first = source.OpenConnectionAsync(TestContext.Current.CancellationToken).AsTask();
        var second = source.OpenConnectionAsync(TestContext.Current.CancellationToken).AsTask();
        await held.DisposeAsync();
        var newcomer = source.OpenConnectionAsync(TestContext.Current.CancellationToken).AsTask();
        var one = await first.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.False(second.IsCompleted);
        Assert.False(newcomer.IsCompleted);
        await one.DisposeAsync();
        var two = await second.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.False(newcomer.IsCompleted);
        await two.DisposeAsync();
        await using var three = await newcomer.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.False(wire.HasOutput());
        Assert.True(wire.Session.IsHealthy);
    }

    [Fact]
    public async Task CancellingHeadAndMiddleWaitersPreservesTheRemainingOrder()
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire);
        var held = await source.OpenConnectionAsync(TestContext.Current.CancellationToken);
        using var headToken = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var middleToken = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var head = source.OpenConnectionAsync(headToken.Token).AsTask();
        var first = source.OpenConnectionAsync(TestContext.Current.CancellationToken).AsTask();
        var middle = source.OpenConnectionAsync(middleToken.Token).AsTask();
        var last = source.OpenConnectionAsync(TestContext.Current.CancellationToken).AsTask();
        headToken.Cancel();
        middleToken.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => head.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => middle.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
        Assert.False(first.IsCompleted);
        Assert.False(last.IsCompleted);
        await held.DisposeAsync();
        var one = await first.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.False(last.IsCompleted);
        await one.DisposeAsync();
        await using var two = await last.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.True(wire.Session.IsHealthy);
    }

    [Fact]
    public async Task CancellationRacingSlotHandoffDoesNotLeakALease()
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire);
        for (int i = 0; i < 64; i++)
        {
            var held = await source.OpenConnectionAsync(TestContext.Current.CancellationToken);
            using var request = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var waiting = source.OpenConnectionAsync(request.Token).AsTask();
            await Task.WhenAll(Task.Run(request.Cancel, TestContext.Current.CancellationToken),
                Task.Run(async () => await held.DisposeAsync(), TestContext.Current.CancellationToken));
            try { await (await waiting).DisposeAsync(); }
            catch (OperationCanceledException) { }
            await using var probe = await source.OpenConnectionAsync(TestContext.Current.CancellationToken)
                .AsTask().WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        }
        Assert.True(wire.Session.IsHealthy);
    }

    [Fact]
    public async Task FactoryFailureHandsCreationCapacityToTheNextWaiter()
    {
        await using var wire = new ScriptedSession();
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        await using var source = new MpgsqlDataSource(async token =>
        {
            int call = Interlocked.Increment(ref calls);
            if (call == 1) await resume.Task.WaitAsync(token);
            if (call < 3) throw new IOException("startup failed");
            return wire.Session;
        }, (_, _) => ValueTask.CompletedTask, new() { MaxConnections = 1 });
        var one = source.OpenConnectionAsync(TestContext.Current.CancellationToken).AsTask();
        var two = source.OpenConnectionAsync(TestContext.Current.CancellationToken).AsTask();
        var three = source.OpenConnectionAsync(TestContext.Current.CancellationToken).AsTask();
        Assert.Equal(1, calls);
        resume.SetResult();
        await Assert.ThrowsAsync<IOException>(() => one.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<IOException>(() => two.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
        await using var lease = await three.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.Equal(3, calls);
        Assert.True(wire.Session.IsHealthy);
    }
}
