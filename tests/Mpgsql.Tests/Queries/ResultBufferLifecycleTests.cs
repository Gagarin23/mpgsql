using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class ResultBufferLifecycleTests
{
    [Theory, InlineData(false, false), InlineData(true, false), InlineData(false, true), InlineData(true, true)]
    public async Task DisposalReleasesBufferedRowsAndAllowsFollowingQuery(bool disposeReader, bool completeBeforeRead)
    {
        var token = TestContext.Current.CancellationToken;
        await using var wire = new ScriptedSession();
        wire.Session.ClaimForDataSource(1024);
        await using var batch = wire.Session.CreateBatch(token);
        await batch.SendQueryAsync("select completed");
        await batch.SendSyncAsync();
        Assert.Equal("PBDES", new string(Tags(await wire.ReadOutputAsync())));
        await wire.WriteAsync(Join(Begin(20), Row(Int64(11)), Row(Int64(12)), Row(Int64(13))));
        if (completeBeforeRead)
        {
            await wire.WriteAsync(Join(Command("SELECT 3"), Ready()));
            await batch.Completion.WaitAsync(TestTimeout, token);
        }
        await using var reader = await batch.ReadResultsAsync();
        Assert.Equal(42, wire.Session.BufferedRowBytes);
        Assert.True(await reader.ReadAsync());
        Assert.Equal(11, reader.GetInt64(0));
        var disposal = (disposeReader ? reader.DisposeAsync() : batch.DisposeAsync()).AsTask();
        if (!completeBeforeRead)
        {
            await wire.WriteAsync(Join(Command("SELECT 3"), Ready()));
        }
        await disposal.WaitAsync(TestTimeout, token);
        Assert.Equal(0, wire.Session.BufferedRowBytes);
        Assert.True(wire.Session.IsIdleAndHealthy);
        await CheckFollowingQuery(wire, token);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task CancellationReleasesBufferedRowsWithoutConsumerMovement(bool completeBeforeRead)
    {
        var token = TestContext.Current.CancellationToken;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
        await using var wire = new ScriptedSession();
        wire.Session.ClaimForDataSource(1024);
        await using var batch = wire.Session.CreateBatch(request.Token);
        await batch.SendQueryAsync("select completed");
        await batch.SendSyncAsync();
        await wire.ReadOutputAsync();
        await wire.WriteAsync(Join(Begin(20), Row(Int64(11)), Row(Int64(12))));
        if (completeBeforeRead)
        {
            await wire.WriteAsync(Join(Command("SELECT 2"), Ready()));
            await batch.Completion.WaitAsync(TestTimeout, token);
        }
        await using var reader = await batch.ReadResultsAsync();
        Assert.Equal(28, wire.Session.BufferedRowBytes);
        request.Cancel();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TestTimeout);
        while (wire.Session.BufferedRowBytes != 0)
        {
            await Task.Delay(1, timeout.Token);
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reader.ReadAsync());
        if (!completeBeforeRead)
        {
            await wire.WriteAsync(Join(Command("SELECT 2"), Ready()));
        }
        await batch.Completion.WaitAsync(TestTimeout, token);
        Assert.True(wire.Session.IsIdleAndHealthy);
        await CheckFollowingQuery(wire, token);
    }

    [Fact]
    public async Task SqlErrorDrainsBufferedRowsAndRecoversAtReadyForQuery()
    {
        var token = TestContext.Current.CancellationToken;
        await using var wire = new ScriptedSession();
        wire.Session.ClaimForDataSource(1024);
        await using var batch = wire.Session.CreateBatch(token);
        await batch.SendQueryAsync("select later error");
        await batch.SendSyncAsync();
        await wire.ReadOutputAsync();
        await wire.WriteAsync(Join(Begin(20), Row(Int64(11)), Row(Int64(12))));
        await using var reader = await batch.ReadResultsAsync();
        Assert.Equal(28, wire.Session.BufferedRowBytes);
        await wire.WriteAsync(Join(Error(), Ready()));
        await Assert.ThrowsAsync<MpgsqlServerException>(() => batch.Completion.WaitAsync(TestTimeout, token));
        await Assert.ThrowsAsync<MpgsqlServerException>(() => reader.ReadAsync().AsTask());
        Assert.Equal(0, wire.Session.BufferedRowBytes);
        Assert.True(wire.Session.IsIdleAndHealthy);
        await CheckFollowingQuery(wire, token);
    }

    private static async Task CheckFollowingQuery(ScriptedSession wire, CancellationToken token)
    {
        await using var batch = wire.Session.CreateBatch(token);
        await batch.SendQueryAsync("select following");
        await batch.SendSyncAsync();
        Assert.Equal("PBDES", new string(Tags(await wire.ReadOutputAsync())));
        await wire.WriteAsync(Join(Query(99), Ready()));
        await using var reader = await batch.ReadResultsAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(99, reader.GetInt64(0));
        Assert.False(await reader.ReadAsync());
        Assert.False(await reader.NextResultAsync());
        await batch.Completion.WaitAsync(TestTimeout, token);
        Assert.Equal(0, wire.Session.BufferedRowBytes);
        Assert.True(wire.Session.IsIdleAndHealthy);
    }
}