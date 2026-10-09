using System.Data.Common;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class AdoStandardReadTaskTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory, InlineData(false, 1), InlineData(false, int.MaxValue), InlineData(true, 1), InlineData(true, int.MaxValue)]
    public async Task StandardTaskReadsKeepPrefetchPendingNullAndCompletionContracts(bool cancellable, int fragment)
    {
        await using var wire = new ScriptedSession();
        await using var source = new MpgsqlDataSource(_ => ValueTask.FromResult(wire.Session), (_, _) => ValueTask.CompletedTask);
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand("select two standard rows");
        var opening = command.ExecuteReaderAsync(Token);
        await Sync(wire);
        var initialWrite = wire.WriteAsync(Join(Begin(20), Row((byte[]?)null)), fragment);
        await using DbDataReader reader = await opening.WaitAsync(TestTimeout, Token);
        using var movement = new CancellationTokenSource();
        var readToken = cancellable ? movement.Token : CancellationToken.None;

        var prefetched = reader.ReadAsync(readToken);
        Assert.True(prefetched.IsCompletedSuccessfully);
        Assert.True(await prefetched);
        Assert.Same(DBNull.Value, reader.GetValue(0));
        Assert.Same(DBNull.Value, reader.GetFieldValue<object>(0));
        Assert.Null(reader.GetFieldValue<long?>(0));

        // No second row has been sent: the standard API must retain the real
        // asynchronous movement rather than manufacturing a completed Task.
        var pending = reader.ReadAsync(readToken);
        Assert.False(pending.IsCompleted);
        await initialWrite.WaitAsync(TestTimeout, Token);
        var remainingWrite = wire.WriteAsync(Join(Row(Int64(42)), Command("SELECT 2"), Ready()), fragment);
        Assert.True(await pending.WaitAsync(TestTimeout, Token));
        Assert.Equal(42L, reader.GetFieldValue<long>(0));
        Assert.Equal(42L, reader.GetValue(0));
        Assert.False(await reader.ReadAsync(readToken));
        Assert.False(await reader.NextResultAsync(readToken));
        await remainingWrite.WaitAsync(TestTimeout, Token);
        var completed = reader.ReadAsync(readToken);
        Assert.True(completed.IsCompletedSuccessfully);
        Assert.False(await completed);
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Fact]
    public async Task CancellationDuringPendingStandardReadPreservesTokenAndWaitsForRecovery()
    {
        await using var wire = new ScriptedSession();
        var cancelSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var source = new MpgsqlDataSource(_ => ValueTask.FromResult(wire.Session), (_, _) =>
        {
            cancelSent.TrySetResult();
            return ValueTask.CompletedTask;
        });
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand("select standard cancellation");
        var opening = command.ExecuteReaderAsync(Token);
        await Sync(wire);
        var initialWrite = wire.WriteAsync(Join(Begin(20), Row(Int64(42))));
        await using DbDataReader reader = await opening.WaitAsync(TestTimeout, Token);
        Assert.True(await reader.ReadAsync(CancellationToken.None));
        using var movement = new CancellationTokenSource();
        var pending = reader.ReadAsync(movement.Token);
        Assert.False(pending.IsCompleted);
        await initialWrite.WaitAsync(TestTimeout, Token);
        movement.Cancel();
        await cancelSent.Task.WaitAsync(TestTimeout, Token);
        Assert.False(pending.IsCompleted); // Recovery still owns the connection.
        var recoveryWrite = wire.WriteAsync(Join(Error("57014"), Ready()), 1);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TestTimeout, Token));
        Assert.Equal(movement.Token, error.CancellationToken);
        await recoveryWrite.WaitAsync(TestTimeout, Token);

        command.CommandText = "select following standard query";
        var next = command.ExecuteScalarAsync<long>(Token).AsTask();
        await Sync(wire);
        var nextWrite = wire.WriteAsync(Join(Query(43), Ready()), 1);
        Assert.Equal(43L, (await next.WaitAsync(TestTimeout, Token)).Value);
        await nextWrite.WaitAsync(TestTimeout, Token);
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    private static async Task Sync(ScriptedSession wire)
    {
        var bytes = new List<byte>();
        do { bytes.AddRange(await wire.ReadOutputAsync()); }
        while (!Tags([.. bytes]).Contains('S'));
    }
}
