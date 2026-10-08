using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class ReaderAwaitContractTests
{
    [Theory, InlineData(false), InlineData(true)]
    public async Task RepeatedPendingAdmissionAndMovementKeepValuesContextAndTaskObservers(bool shareTask)
    {
        var token = TestContext.Current.CancellationToken;
        var context = new AsyncLocal<string?>();
        await using var wire = new ScriptedSession();
        await using var source = new MpgsqlMultiplexingDataSource(_ => ValueTask.FromResult(wire.Session), new MpgsqlMultiplexingOptions {MaxConnections = 1, MaxInFlightPerConnection = 1});

        for (var i = 0; i < 32; i++)
        {
            context.Value = $"caller-{i}";
            var held = await MultiplexingLease.HoldAsync(wire, source, token);
            var opening = source.ExecuteReaderAsync("select $1::bigint",
                new[] {MpgsqlParameterValue.Int64(i)}, token);
            Assert.False(opening.IsCompleted);
            var opened = AwaitOnceAsync(opening, shareTask, token);
            await held.DisposeAsync();
            var tags = new List<char>();
            do { tags.AddRange(Tags(await wire.ReadOutputAsync())); } while (!tags.Contains('S'));
            Assert.Equal("PBDES", new string([.. tags]));
            Assert.False(opened.IsCompleted);

            await wire.WriteAsync(Begin(20), 1);
            await using var reader = await opened.WaitAsync(TestTimeout, token);
            Assert.Equal(0, reader.QueryIndex);
            Assert.Equal(context.Value, $"caller-{i}");
            var movement = reader.ReadAsync();
            Assert.False(movement.IsCompleted);
            var moved = AwaitOnceAsync(movement, shareTask, token);
            await wire.WriteAsync(Row(Int64(i)), 1);
            Assert.True(await moved.WaitAsync(TestTimeout, token));
            Assert.Equal(i, reader.GetInt64(0));
            Assert.Equal($"caller-{i}", context.Value);

            var ending = reader.NextResultAsync();
            Assert.False(ending.IsCompleted);
            var ended = AwaitOnceAsync(ending, shareTask, token);
            await wire.WriteAsync(Join(Command(), Ready()), 1);
            Assert.False(await ended.WaitAsync(TestTimeout, token));
            await reader.DisposeAsync();
            Assert.Equal($"caller-{i}", context.Value);
            Assert.True(wire.Session.IsIdleAndHealthy);
            Assert.Equal(0, wire.Session.BufferedRowBytes);
        }
    }

    private static async Task<T> AwaitOnceAsync<T>(ValueTask<T> operation, bool shareTask,
        CancellationToken token)
    {
        if (!shareTask)
        {
            return await operation;
        }
        // Convert once. Multiple observers are supported by the resulting Task, not by ValueTask.
        var shared = operation.AsTask();
        var values = await Task.WhenAll(shared, shared).WaitAsync(TestTimeout, token);
        Assert.Equal(values[0], values[1]);
        return values[0];
    }
}