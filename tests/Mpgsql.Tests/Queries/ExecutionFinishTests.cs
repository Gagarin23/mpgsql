using Mpgsql.Internal;
using Mpgsql.Multiplexing.Internal;
using QueryExecution = Mpgsql.Multiplexing.Internal.QueryExecution;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class ExecutionFinishTests
{
    [Fact]
    public async Task ConcurrentFinishCallsRecoverAndReleaseTheLeaseOnlyOnce()
    {
        await using var wire = new ScriptedSession();
        await using var source = new MpgsqlMultiplexingDataSource
        (
            _ => ValueTask.FromResult(wire.Session), new MpgsqlMultiplexingOptions
            {
                MaxConnections = 1
            }
        );
        var pooled = new PooledSession(wire.Session);
        for (var round = 0;
             round < 32;
             round++)
        {
            pooled.Active = 1;
            var execution = new QueryExecution
            (
                source, pooled, new QueryDefinition("select 1::bigint", default),
                TestContext.Current.CancellationToken
            );
            var output = new List<byte>();
            while (Tags([.. output])
                       .Count(tag => tag == 'S') == 0)
            {
                output.AddRange(await wire.ReadOutputAsync());
            }
            var pending = Enumerable
                .Range(0, 32)
                .Select
                (_ => Task.Run
                    (
                        async () =>
                            await execution.FinishAsync(true), TestContext.Current.CancellationToken
                    )
                )
                .ToArray();
            await wire.WriteAsync(Join(Query(1), Ready()), round % 2 == 0 ? 1 : int.MaxValue);
            await Task
                .WhenAll(pending)
                .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            Assert.Equal(0, pooled.Active);
            await execution.FinishAsync(false);
            await execution.FinishAsync(true);
            Assert.Equal(0, pooled.Active);
            Assert.True(wire.Session.IsIdleAndHealthy);
        }
    }

    [Fact]
    public async Task NormalFinishKeepsEndOfReaderReadableUntilItsOwnerDisposes()
    {
        await using var wire = new ScriptedSession();
        await using var source = new MpgsqlMultiplexingDataSource
        (
            _ => ValueTask.FromResult(wire.Session), new MpgsqlMultiplexingOptions
            {
                MaxConnections = 1
            }
        );
        var pooled = new PooledSession(wire.Session)
        {
            Active = 1
        };
        var execution = new QueryExecution
        (
            source, pooled, new QueryDefinition("select 3::bigint", default),
            TestContext.Current.CancellationToken
        );
        var opening = execution
            .OpenReaderAsync()
            .AsTask();
        var output = new List<byte>();
        while (Tags([.. output])
                   .Count(tag => tag == 'S') == 0)
        {
            output.AddRange(await wire.ReadOutputAsync());
        }
        await wire.WriteAsync(Join(Query(3), Ready()));
        await using var reader = await opening.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.True
        (
            await reader
                .ReadAsync()
                .AsTask()
        );
        Assert.Equal(3, reader.GetInt64(0));
        Assert.False
        (
            await reader
                .ReadAsync()
                .AsTask()
        );
        Assert.False(await reader.NextResultAsync());
        Assert.Equal(0, pooled.Active);
        Assert.False
        (
            await reader
                .ReadAsync()
                .AsTask()
        );
        await execution.FinishAsync(true);
        await Assert.ThrowsAsync<ObjectDisposedException>
        (() => reader
            .ReadAsync()
            .AsTask()
        );
        Assert.Equal(0, pooled.Active);
    }
}