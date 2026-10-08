using Mpgsql.Internal;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class ExecutionFinishTests
{
    [Fact]
    public async Task ConcurrentFinishCallsRecoverAndReleaseTheLeaseOnlyOnce()
    {
        await using var wire = new ScriptedSession();
        await using var source = new MpgsqlDataSource(_ => ValueTask.FromResult(wire.Session),
            (_, _) => ValueTask.CompletedTask, new() { MaxConnections = 1 });
        var pooled = new PooledSession(wire.Session);
        for (int round = 0; round < 32; round++)
        {
            pooled.Active = 1;
            var execution = new QueryExecution(source, pooled, new("select 1::bigint", default),
                TestContext.Current.CancellationToken);
            var output = new List<byte>();
            while (Tags([.. output]).Count(tag => tag == 'S') == 0) output.AddRange(await wire.ReadOutputAsync());
            var pending = Enumerable.Range(0, 32).Select(_ => Task.Run(async () =>
                await execution.FinishAsync(discard: true), TestContext.Current.CancellationToken)).ToArray();
            await wire.WriteAsync(Join(Query(1), Ready()), fragment: round % 2 == 0 ? 1 : int.MaxValue);
            await Task.WhenAll(pending).WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            Assert.Equal(0, pooled.Active);
            await execution.FinishAsync(discard: false);
            await execution.FinishAsync(discard: true);
            Assert.Equal(0, pooled.Active);
            Assert.True(wire.Session.IsIdleAndHealthy);
        }
    }

    [Fact]
    public async Task NormalFinishKeepsEndOfReaderReadableUntilItsOwnerDisposes()
    {
        await using var wire = new ScriptedSession();
        await using var source = new MpgsqlDataSource(_ => ValueTask.FromResult(wire.Session),
            (_, _) => ValueTask.CompletedTask, new() { MaxConnections = 1 });
        var pooled = new PooledSession(wire.Session) { Active = 1 };
        var execution = new QueryExecution(source, pooled, new("select 3::bigint", default),
            TestContext.Current.CancellationToken);
        var opening = execution.OpenReaderAsync().AsTask();
        var output = new List<byte>();
        while (Tags([.. output]).Count(tag => tag == 'S') == 0) output.AddRange(await wire.ReadOutputAsync());
        await wire.WriteAsync(Join(Query(3), Ready()));
        await using var reader = await opening.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync());
        Assert.Equal(3, reader.GetInt64(0));
        Assert.False(await reader.ReadAsync());
        Assert.False(await reader.NextResultAsync());
        Assert.Equal(0, pooled.Active);
        Assert.False(await reader.ReadAsync());
        await execution.FinishAsync(discard: true);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => reader.ReadAsync().AsTask());
        Assert.Equal(0, pooled.Active);
    }
}
