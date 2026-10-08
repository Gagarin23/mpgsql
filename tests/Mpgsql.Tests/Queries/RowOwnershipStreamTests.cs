using System.Buffers;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class RowOwnershipStreamTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public async Task RowsKeepTheirValuesUnderFragmentationAndBackpressure(int fragment)
    {
        await using var wire = new ScriptedSession();
        wire.Session.ClaimForDataSource(128);
        await using var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await batch.SendQueryAsync("select many");
        await batch.SendSyncAsync();
        await wire.ReadOutputAsync();
        var frames = new List<byte[]> { Begin(20) };
        for (int i = 0; i < 512; i++) frames.Add(Row(Int64(i)));
        frames.Add(Command("SELECT 512"));
        frames.Add(Ready());
        var writing = wire.WriteAsync(Join([.. frames]), fragment);
        await using var reader = await batch.ReadResultsAsync().AsTask()
            .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        for (int i = 0; i < 512; i++)
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(i, reader.GetInt64(0));
        }
        Assert.False(await reader.ReadAsync());
        Assert.False(await reader.NextResultAsync());
        await reader.DisposeAsync();
        await batch.DisposeAsync();
        await writing.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.Equal(0, wire.Session.BufferedRowBytes);
        Assert.True(wire.Session.IsIdleAndHealthy);
        await FollowingQueryAsync(wire);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public async Task HeldRowRemainsValidWhileAnotherBatchConsumesLaterInput(int fragment)
    {
        await using var wire = new ScriptedSession();
        wire.Session.ClaimForDataSource(32 * 1024);
        await using var first = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await first.SendQueryAsync("select held");
        await first.SendSyncAsync();
        await wire.ReadOutputAsync();
        await wire.WriteAsync(Join(Query(111), Ready()), fragment);
        await using var firstReader = await first.ReadResultsAsync();
        Assert.True(await firstReader.ReadAsync());
        var held = firstReader.GetRawValue(0)!.Value;
        await using var second = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await second.SendQueryAsync("select following rows");
        await second.SendSyncAsync();
        await wire.ReadOutputAsync();
        var frames = new List<byte[]> { Begin(20) };
        for (int i = 0; i < 1024; i++) frames.Add(Row(Int64(i + 1000L)));
        frames.Add(Command("SELECT 1024"));
        frames.Add(Ready());
        var writing = wire.WriteAsync(Join([.. frames]), fragment);
        await using var secondReader = await second.ReadResultsAsync().AsTask()
            .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        for (int i = 0; i < 1024; i++)
        {
            Assert.True(await secondReader.ReadAsync());
            Assert.Equal(i + 1000L, secondReader.GetInt64(0));
            if (i % 128 == 0) Assert.Equal(Int64(111), held.ToArray());
        }
        Assert.False(await secondReader.ReadAsync());
        Assert.False(await secondReader.NextResultAsync());
        await writing.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.Equal(Int64(111), held.ToArray());
        Assert.Equal(111, firstReader.GetInt64(0));
        Assert.False(await firstReader.ReadAsync());
        Assert.False(await firstReader.NextResultAsync());
        await secondReader.DisposeAsync();
        await firstReader.DisposeAsync();
        await second.DisposeAsync();
        await first.DisposeAsync();
        Assert.Equal(0, wire.Session.BufferedRowBytes);
        Assert.True(wire.Session.IsIdleAndHealthy);
        await FollowingQueryAsync(wire);
    }

    private static async Task FollowingQueryAsync(ScriptedSession wire)
    {
        await using var following = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await following.SendQueryAsync("select following");
        await following.SendSyncAsync();
        await wire.ReadOutputAsync();
        await wire.WriteAsync(Join(Query(777), Ready()));
        await using var reader = await following.ReadResultsAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(777, reader.GetInt64(0));
        Assert.False(await reader.ReadAsync());
        Assert.False(await reader.NextResultAsync());
    }
}
