using System.Buffers;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class FlushCoalescingTests
{
    [Fact]
    public async Task ReadySendsShareFlushButRetainTheirCompletionBarrier()
    {
        using var input = new BlockingInputMemory(true);
        await using var wire = new ScriptedSession(true);
        await using var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        var first = batch.SendQueryAsync("select $1", new[] {MpgsqlParameterValue.Int64Array(input.Memory)}).AsTask();
        await input.Entered.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        var second = batch.SendQueryAsync("select 2").AsTask();
        var sync = batch.SendSyncAsync().AsTask();
        input.Resume();

        var held = await wire.Outgoing.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("PBDEPBDES", new string(Tags(held.Buffer.ToArray())));
        Assert.True(batch.Sealed.IsCompletedSuccessfully);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        Assert.False(sync.IsCompleted);
        wire.Outgoing.Reader.AdvanceTo(held.Buffer.End);
        await Task.WhenAll(first, second, sync).WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        input.Revoke();
        await wire.WriteAsync(Join(Query(1), Query(2), Ready()));
        await using var reader = await batch.ReadResultsAsync();
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(i, reader.QueryIndex);
            Assert.True(await reader.ReadAsync());
            Assert.Equal(i + 1, reader.GetInt64(0));
            Assert.False(await reader.ReadAsync());
            Assert.Equal(i == 0, await reader.NextResultAsync());
        }
        await batch.Completion.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.True(wire.Session.IsHealthy);
    }

    [Fact]
    public async Task SharedFlushFailureReachesAllSendsAndTheSession()
    {
        using var input = new BlockingInputMemory(true);
        await using var wire = new ScriptedSession(true);
        var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        var first = batch.SendQueryAsync("select $1", new[] {MpgsqlParameterValue.Int64Array(input.Memory)}).AsTask();
        await input.Entered.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        var second = batch.SendQueryAsync("select 2").AsTask();
        var sync = batch.SendSyncAsync().AsTask();
        input.Resume();
        var held = await wire.Outgoing.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("PBDEPBDES", new string(Tags(held.Buffer.ToArray())));
        var cause = new IOException("shared output failure");
        await wire.Outgoing.Reader.CompleteAsync(cause);
        var failure = await Assert.ThrowsAnyAsync<IOException>(() => first.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
        foreach (var task in new[] {second, sync, batch.Completion, wire.Session.Completion})
            Assert.Same(failure, await Assert.ThrowsAnyAsync<IOException>(() => task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken)));
        Assert.False(wire.Session.IsHealthy);
    }
}