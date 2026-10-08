using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class FirstResultTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public async Task PendingFirstResultKeepsFieldsRowsAndQueryIndices(int fragment)
    {
        await using var wire = new ScriptedSession();
        await using var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await batch.SendQueryAsync("select first");
        await batch.SendQueryAsync("select second");
        await batch.SendSyncAsync();
        await wire.ReadOutputAsync();
        var pending = batch.ReadResultsAsync().AsTask();
        Assert.False(pending.IsCompleted);
        await wire.WriteAsync(Join(Begin(20), Row((byte[]?)null), Row(Int64(2)), Command("SELECT 2"),
            Query(99), Ready()), fragment);
        await using var reader = await pending.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.Equal(0, reader.QueryIndex);
        Assert.Equal(20u, Assert.Single(reader.Columns.ToArray()).DataTypeOid);
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.IsDBNull(0));
        Assert.True(await reader.ReadAsync());
        Assert.Equal(2, reader.GetInt64(0));
        Assert.False(await reader.ReadAsync());
        Assert.Equal("SELECT 2", reader.CommandTag);
        Assert.True(await reader.NextResultAsync());
        Assert.Equal(1, reader.QueryIndex);
        Assert.True(await reader.ReadAsync());
        Assert.Equal(99, reader.GetInt64(0));
        Assert.False(await reader.ReadAsync());
        Assert.False(await reader.NextResultAsync());
        await batch.Completion.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Fact]
    public async Task FirstResultCannotDeadlockTheBudgetBeforeCommandComplete()
    {
        await using var wire = new ScriptedSession();
        wire.Session.ClaimForDataSource(14); // One bigint DataRow payload fits; the next must wait.
        await using var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await batch.SendQueryAsync("select rows");
        await batch.SendSyncAsync();
        await wire.ReadOutputAsync();
        var pending = batch.ReadResultsAsync().AsTask();
        var writing = wire.WriteAsync(Join(Begin(20), Row(Int64(1)), Row(Int64(2)), Row(Int64(3)),
            Command("SELECT 3"), Ready()));
        await using var reader = await pending.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt64(0));
        // Disposal must drain the remaining rows and unblock the waiting network reader.
        await reader.DisposeAsync().AsTask().WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        await writing.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        await batch.DisposeAsync();
        Assert.Equal(0, wire.Session.BufferedRowBytes);
        await FollowingQueryAsync(wire);
    }

    [Fact]
    public async Task ErrorBeforeDescriptionWaitsForReadyAndLeavesTheNextQueryUsable()
    {
        await using var wire = new ScriptedSession();
        wire.Session.ClaimForDataSource(64);
        await using var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await batch.SendQueryAsync("select failing");
        await batch.SendSyncAsync();
        await wire.ReadOutputAsync();
        var pending = batch.ReadResultsAsync().AsTask();
        await wire.WriteAsync(Error());
        Assert.False(pending.IsCompleted);
        await wire.WriteAsync(Ready());
        var error = await Assert.ThrowsAsync<MpgsqlServerException>(() => pending.WaitAsync(TestTimeout,
            TestContext.Current.CancellationToken));
        Assert.Equal(0, error.QueryIndex);
        Assert.Equal(Mpgsql.Protocol.TransactionStatus.Idle, error.TransactionStatus);
        Assert.True(batch.ConsumerDisposed);
        Assert.Equal(0, wire.Session.BufferedRowBytes);
        await FollowingQueryAsync(wire);
    }

    [Fact]
    public async Task PendingCancellationKeepsCanceledStatusAndDrainsThePublishedBoundary()
    {
        await using var wire = new ScriptedSession();
        wire.Session.ClaimForDataSource(64);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var batch = wire.Session.CreateBatch(request.Token);
        await batch.SendQueryAsync("select cancelled");
        await batch.SendSyncAsync();
        await wire.ReadOutputAsync();
        var pending = batch.ReadResultsAsync().AsTask();
        request.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TestTimeout,
            TestContext.Current.CancellationToken));
        Assert.Equal(request.Token, error.CancellationToken);
        Assert.True(pending.IsCanceled);
        Assert.True(batch.ConsumerDisposed);
        await wire.WriteAsync(Join(Query(7), Ready()));
        await batch.Completion.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.Equal(0, wire.Session.BufferedRowBytes);
        await FollowingQueryAsync(wire);
    }

    [Fact]
    public async Task EmptyGroupReadDoesNotSendSyncAndFinishesAtTheExplicitBoundary()
    {
        await using var wire = new ScriptedSession();
        await using var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        var pending = batch.ReadResultsAsync().AsTask();
        Assert.False(pending.IsCompleted);
        Assert.False(wire.HasOutput());
        await batch.SendSyncAsync();
        Assert.Equal(new[] { 'S' }, Tags(await wire.ReadOutputAsync()));
        await wire.WriteAsync(Ready());
        await using var reader = await pending.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.Equal(-1, reader.QueryIndex);
        Assert.True(reader.Columns.IsEmpty);
        Assert.False(await reader.ReadAsync());
        Assert.False(await reader.NextResultAsync());
        await FollowingQueryAsync(wire);
    }

    [Fact]
    public async Task BlockingUserContinuationDoesNotBlockTheNetworkReader()
    {
        await using var wire = new ScriptedSession();
        await using var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await batch.SendQueryAsync("select one");
        await batch.SendSyncAsync();
        await wire.ReadOutputAsync();
        var pending = batch.ReadResultsAsync().AsTask();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var callback = pending.ContinueWith(_ =>
        {
            entered.TrySetResult();
            Assert.True(release.Wait(TestTimeout));
        }, TestContext.Current.CancellationToken, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            await wire.WriteAsync(Begin(20));
            await entered.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            // The first-result callback remains blocked while the same transport processes RFQ.
            await wire.WriteAsync(Join(Row(Int64(9)), Command(), Ready()));
            await batch.Completion.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        }
        finally { release.Set(); }
        await callback.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        await using var reader = await pending;
        Assert.True(await reader.ReadAsync());
        Assert.Equal(9, reader.GetInt64(0));
        Assert.False(await reader.ReadAsync());
        Assert.False(await reader.NextResultAsync());
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    private static async Task FollowingQueryAsync(ScriptedSession wire)
    {
        await using var next = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await next.SendQueryAsync("select next");
        await next.SendSyncAsync();
        await wire.ReadOutputAsync();
        await wire.WriteAsync(Join(Query(123), Ready()));
        await using var reader = await next.ReadResultsAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(123, reader.GetInt64(0));
        Assert.False(await reader.ReadAsync());
        Assert.False(await reader.NextResultAsync());
        await next.Completion.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.True(wire.Session.IsIdleAndHealthy);
        Assert.Equal(0, wire.Session.BufferedRowBytes);
    }
}
