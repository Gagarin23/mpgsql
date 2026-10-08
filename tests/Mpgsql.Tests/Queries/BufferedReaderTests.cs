using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class BufferedReaderTests
{
    [Fact]
    public async Task BufferedRowsKeepNullValuesEndTagsAndNextQueryBoundaries()
    {
        await using var wire = new ScriptedSession();
        await using var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await batch.SendQueryAsync("select first");
        await batch.SendQueryAsync("select second");
        await batch.SendSyncAsync();
        await wire.ReadOutputAsync();
        await wire.WriteAsync
        (
            Join
            (
                Begin(20), Row(Int64(1)), Row((byte[]?)null), Row(Int64(3)),
                Command("SELECT 3"), Query(4), Ready()
            )
        );
        await batch.Completion.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        await using var reader = await batch.ReadResultsAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt64(0));
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.IsDBNull(0));
        Assert.Null(reader.GetInt64(0));
        Assert.True(await reader.ReadAsync());
        Assert.Equal(3, reader.GetInt64(0));
        Assert.False(await reader.ReadAsync());
        Assert.Equal("SELECT 3", reader.CommandTag);
        Assert.False(await reader.ReadAsync());
        Assert.True(await reader.NextResultAsync());
        Assert.Equal(1, reader.QueryIndex);
        Assert.True(await reader.ReadAsync());
        Assert.Equal(4, reader.GetInt64(0));
        Assert.False(await reader.ReadAsync());
        Assert.False(await reader.NextResultAsync());
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Fact]
    public async Task CancellationWhileRowsAreBufferedDiscardsEveryReservation()
    {
        await using var wire = new ScriptedSession();
        wire.Session.ClaimForDataSource(1024);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var batch = wire.Session.CreateBatch(request.Token);
        await batch.SendQueryAsync("select values");
        await batch.SendSyncAsync();
        await wire.ReadOutputAsync();
        await wire.WriteAsync
        (
            Join
            (
                Begin(20), Row(Int64(1)), Row(Int64(2)), Row(Int64(3)),
                Command("SELECT 3"), Ready()
            )
        );
        await batch.Completion.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        await using var reader = await batch.ReadResultsAsync();
        Assert.True(await reader.ReadAsync());
        request.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>
        (() => reader
            .ReadAsync()
            .AsTask()
        );
        await reader.DisposeAsync();
        await batch.DisposeAsync();
        Assert.Equal(0, wire.Session.BufferedRowBytes);
        Assert.True(wire.Session.IsIdleAndHealthy);
    }
}