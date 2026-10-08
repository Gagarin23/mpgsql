using Mpgsql.Protocol;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class ResultMovementTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public async Task BufferedDescriptionsKeepNoDataEmptyRowsAndNullColumnsDistinct(int fragment)
    {
        await using var wire = new ScriptedSession();
        await using var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        for (int i = 0; i < 3; i++) await batch.SendQueryAsync("select result");
        await batch.SendSyncAsync();
        await wire.ReadOutputAsync();
        await wire.WriteAsync(Join(Packet('1'), Packet('2'), Packet('n'), Command("UPDATE 2"),
            Begin(), Row(), Command(), Begin(20), Row((byte[]?)null), Command(), Ready()), fragment);
        await batch.Completion.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        await using var reader = await batch.ReadResultsAsync();
        Assert.Equal(0, reader.QueryIndex);
        Assert.False(reader.IsRowSet);
        Assert.False(await reader.ReadAsync());
        Assert.Equal("UPDATE 2", reader.CommandTag);

        Assert.True(await reader.NextResultAsync());
        Assert.Equal(1, reader.QueryIndex);
        Assert.True(reader.IsRowSet);
        Assert.True(reader.Columns.IsEmpty);
        Assert.Null(reader.CommandTag);
        Assert.True(await reader.ReadAsync());
        Assert.False(await reader.ReadAsync());

        Assert.True(await reader.NextResultAsync());
        Assert.Equal(2, reader.QueryIndex);
        Assert.Equal(20u, reader.Columns.Span[0].DataTypeOid);
        Assert.Null(reader.CommandTag);
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.IsDBNull(0));
        Assert.False(await reader.ReadAsync());
        Assert.False(await reader.NextResultAsync());
        Assert.False(await reader.NextResultAsync());
        await FollowingQueryAsync(wire);
    }

    [Fact]
    public async Task SwitchingBeforeTheEndDrainsUnreadRowsUnderBackpressure()
    {
        await using var wire = new ScriptedSession();
        wire.Session.ClaimForDataSource(14);
        await using var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await batch.SendQueryAsync("select first");
        await batch.SendQueryAsync("select second");
        await batch.SendSyncAsync();
        await wire.ReadOutputAsync();
        var writing = wire.WriteAsync(Join(Begin(20), Row(Int64(1)), Row(Int64(2)), Row(Int64(3)),
            Command("SELECT 3"), Query(99), Ready()), 1);
        await using var reader = await batch.ReadResultsAsync().AsTask().WaitAsync(TestTimeout,
            TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt64(0));
        Assert.True(await reader.NextResultAsync().AsTask().WaitAsync(TestTimeout,
            TestContext.Current.CancellationToken));
        Assert.Equal(1, reader.QueryIndex);
        Assert.True(await reader.ReadAsync());
        Assert.Equal(99, reader.GetInt64(0));
        Assert.False(await reader.ReadAsync());
        Assert.False(await reader.NextResultAsync());
        await writing.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        await FollowingQueryAsync(wire);
    }

    [Fact]
    public async Task ErrorDuringPendingNextResultWaitsForReadyAndKeepsItsQueryIndex()
    {
        await using var wire = new ScriptedSession();
        await using var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await batch.SendQueryAsync("select first");
        await batch.SendQueryAsync("select failing");
        await batch.SendSyncAsync();
        await wire.ReadOutputAsync();
        await wire.WriteAsync(Query(1));
        await using var reader = await batch.ReadResultsAsync();
        Assert.True(await reader.ReadAsync());
        Assert.False(await reader.ReadAsync());
        Task<bool> next = reader.NextResultAsync().AsTask();
        Assert.False(next.IsCompleted);
        await wire.WriteAsync(Error());
        Assert.False(next.IsCompleted);
        await wire.WriteAsync(Ready());
        var error = await Assert.ThrowsAsync<MpgsqlServerException>(() => next.WaitAsync(TestTimeout,
            TestContext.Current.CancellationToken));
        Assert.Equal(1, error.QueryIndex);
        Assert.Equal(TransactionStatus.Idle, error.TransactionStatus);
        await FollowingQueryAsync(wire);
    }

    [Fact]
    public async Task PendingNextResultCancellationKeepsCanceledTaskStatus()
    {
        await using var wire = new ScriptedSession();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var batch = wire.Session.CreateBatch(request.Token);
        await batch.SendQueryAsync("select first");
        await batch.SendQueryAsync("select second");
        await batch.SendSyncAsync();
        await wire.ReadOutputAsync();
        await wire.WriteAsync(Query(1));
        await using var reader = await batch.ReadResultsAsync();
        Assert.True(await reader.ReadAsync());
        Assert.False(await reader.ReadAsync());
        Task<bool> next = reader.NextResultAsync().AsTask();
        Assert.False(next.IsCompleted);
        request.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next.WaitAsync(TestTimeout,
            TestContext.Current.CancellationToken));
        Assert.True(next.IsCanceled);
        await wire.WriteAsync(Join(Query(2), Ready()));
        await batch.Completion.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        await FollowingQueryAsync(wire);
    }

    private static async Task FollowingQueryAsync(ScriptedSession wire)
    {
        Assert.Equal(0, wire.Session.BufferedRowBytes);
        Assert.True(wire.Session.IsIdleAndHealthy);
        await using var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await batch.SendQueryAsync("select following");
        await batch.SendSyncAsync();
        await wire.ReadOutputAsync();
        await wire.WriteAsync(Join(Query(777), Ready()));
        await using var reader = await batch.ReadResultsAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(777, reader.GetInt64(0));
        Assert.False(await reader.ReadAsync());
        Assert.False(await reader.NextResultAsync());
        await batch.Completion.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.Equal(0, wire.Session.BufferedRowBytes);
        Assert.True(wire.Session.IsIdleAndHealthy);
    }
}
