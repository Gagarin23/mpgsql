using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class ReceiveBurstTests
{
    [Fact]
    public async Task CommandEndWakesAtAvailableBurstEndBeforeTheFollowingQueryOrSync()
    {
        var token = TestContext.Current.CancellationToken;
        await using var wire = new ScriptedSession();
        await using var batch = wire.Session.CreateBatch(token);
        await batch.SendQueryAsync("select first");
        await batch.SendQueryAsync("select second");
        Assert.Equal("PBDEPBDE", new string(Tags(await wire.ReadOutputAsync())));
        var opening = batch.ReadResultsAsync().AsTask();
        await wire.WriteAsync(Query(11));
        await using var reader = await opening.WaitAsync(TestTimeout, token);
        Assert.True(await reader.ReadAsync());
        Assert.Equal(11, reader.GetInt64(0));
        Assert.False(await reader.ReadAsync().AsTask().WaitAsync(TestTimeout, token));
        Assert.Equal(0, reader.QueryIndex);
        Assert.Equal("SELECT 1", reader.CommandTag);
        Assert.False(batch.Completion.IsCompleted);
        Assert.False(wire.HasOutput());
        var next = reader.NextResultAsync().AsTask();
        Assert.False(next.IsCompleted);
        await batch.SendSyncAsync();
        Assert.Equal("S", new string(Tags(await wire.ReadOutputAsync())));
        await wire.WriteAsync(Join(Query(12), Ready()), 7);
        Assert.True(await next.WaitAsync(TestTimeout, token));
        Assert.Equal(1, reader.QueryIndex);
        Assert.True(await reader.ReadAsync());
        Assert.Equal(12, reader.GetInt64(0));
        Assert.False(await reader.ReadAsync());
        Assert.False(await reader.NextResultAsync());
        await batch.Completion.WaitAsync(TestTimeout, token);
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Theory, InlineData(false, int.MaxValue), InlineData(true, int.MaxValue), InlineData(false, 1), InlineData(true, 1)]
    public async Task CommandEndsInOneBatchDoNotHideRowsNeededToReleaseCapacity(bool discard, int fragment)
    {
        var token = TestContext.Current.CancellationToken;
        await using var wire = new ScriptedSession();
        wire.Session.ClaimForDataSource(14);
        await using var batch = wire.Session.CreateBatch(token);
        await batch.SendQueryAsync("select first");
        await batch.SendQueryAsync("select second");
        await batch.SendSyncAsync();
        Assert.Equal("PBDEPBDES", new string(Tags(await wire.ReadOutputAsync())));
        var opening = batch.ReadResultsAsync().AsTask();
        var writing = wire.WriteAsync(Join(Query(11), Query(12), Ready()), fragment);
        await using var reader = await opening.WaitAsync(TestTimeout, token);
        Assert.True(await reader.ReadAsync());
        Assert.Equal(11, reader.GetInt64(0));
        if (discard)
        {
            await reader.DisposeAsync().AsTask().WaitAsync(TestTimeout, token);
        }
        else
        {
            Assert.False(await reader.ReadAsync().AsTask().WaitAsync(TestTimeout, token));
            Assert.True(await reader.NextResultAsync().AsTask().WaitAsync(TestTimeout, token));
            Assert.Equal(1, reader.QueryIndex);
            Assert.True(await reader.ReadAsync());
            Assert.Equal(12, reader.GetInt64(0));
            Assert.False(await reader.ReadAsync());
            Assert.False(await reader.NextResultAsync());
        }
        await writing.WaitAsync(TestTimeout, token);
        await batch.Completion.WaitAsync(TestTimeout, token);
        await batch.DisposeAsync();
        Assert.Equal(0, wire.Session.BufferedRowBytes);
        Assert.True(wire.Session.IsIdleAndHealthy);
        await using var following = wire.Session.CreateBatch(token);
        await following.SendQueryAsync("select following");
        await following.SendSyncAsync();
        Assert.Equal("PBDES", new string(Tags(await wire.ReadOutputAsync())));
        await wire.WriteAsync(Join(Query(21), Ready()), fragment);
        await using var followingReader = await following.ReadResultsAsync();
        Assert.True(await followingReader.ReadAsync());
        Assert.Equal(21, followingReader.GetInt64(0));
        Assert.False(await followingReader.NextResultAsync());
        await followingReader.DisposeAsync();
        await following.Completion.WaitAsync(TestTimeout, token);
        Assert.Equal(0, wire.Session.BufferedRowBytes);
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Theory, InlineData(int.MaxValue), InlineData(1), InlineData(7)]
    public async Task PartialResponsesWakeReaderWithoutRequiringSyncOrAnotherRead(int fragment)
    {
        await using var wire = new ScriptedSession();
        await using var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await batch.SendQueryAsync("select 11::bigint");
        Assert.Equal("PBDE", new string(Tags(await wire.ReadOutputAsync())));
        var opening = batch.ReadResultsAsync().AsTask();
        await wire.WriteAsync(Begin(20), fragment);
        await using var reader = await opening.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        var reading = reader.ReadAsync().AsTask();
        await wire.WriteAsync(Row(Int64(11)), fragment);
        Assert.True(await reading.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
        Assert.Equal(11, reader.GetInt64(0));
        var ending = reader.ReadAsync().AsTask();
        await wire.WriteAsync(Command(), fragment);
        Assert.False(await ending.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
        Assert.False(batch.Completion.IsCompleted);
        Assert.False(wire.HasOutput());
        await batch.SendSyncAsync();
        Assert.Equal("S", new string(Tags(await wire.ReadOutputAsync())));
        await wire.WriteAsync(Ready(), fragment);
        Assert.False(await reader.NextResultAsync().AsTask().WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
        Assert.True(wire.Session.IsHealthy);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task FullBudgetWakesReaderAndNextGroupSurvivesConsumptionOrEarlyDispose(bool discard)
    {
        await using var wire = new ScriptedSession();
        wire.Session.ClaimForDataSource(14); // one bigint DataRow payload
        await using var first = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await using var second = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await first.SendQueryAsync("select first");
        await first.SendSyncAsync();
        await second.SendQueryAsync("select second");
        await second.SendSyncAsync();
        Assert.Equal("PBDESPBDES", new string(Tags(await wire.ReadOutputAsync())));
        var opening = first.ReadResultsAsync().AsTask();
        var nextOpening = second.ReadResultsAsync().AsTask();
        var writing = wire.WriteAsync(Join(Begin(20), Row(Int64(11)), Row(Int64(12)),
            Command("SELECT 2"), Ready(), Query(21), Ready()));
        await using var reader = await opening.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync());
        Assert.Equal(11, reader.GetInt64(0));
        Assert.Equal(14, wire.Session.BufferedRowBytes);
        if (discard)
        {
            await reader.DisposeAsync();
        }
        else
        {
            Assert.True(await reader.ReadAsync().AsTask().WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
            Assert.Equal(12, reader.GetInt64(0));
            Assert.False(await reader.ReadAsync());
            Assert.False(await reader.NextResultAsync().AsTask().WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
        }
        await using var next = await nextOpening.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.True(await next.ReadAsync());
        Assert.Equal(21, next.GetInt64(0));
        Assert.False(await next.ReadAsync());
        Assert.False(await next.NextResultAsync().AsTask().WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
        await writing.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        await first.Completion.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        await second.Completion.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.Equal(0, wire.Session.BufferedRowBytes);
        Assert.True(wire.Session.IsIdleAndHealthy);
    }
}