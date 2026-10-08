using Mpgsql.Protocol;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class SessionObservationTests
{
    [Theory, InlineData(1), InlineData(int.MaxValue)]
    public async Task ReadyPublishesTransactionStateBeforeCompletingEachGroup(int fragment)
    {
        var token = TestContext.Current.CancellationToken;
        await using var wire = new ScriptedSession();
        for (var i = 0; i < 24; i++)
        {
            var status = (i % 3) switch
            {
                0 => TransactionStatus.InTransaction,
                1 => TransactionStatus.FailedTransaction,
                _ => TransactionStatus.Idle
            };
            var batch = wire.Session.CreateBatch(token);
            try
            {
                await batch.SendQueryAsync("select state");
                await batch.SendSyncAsync();
                await wire.ReadOutputAsync();
                await wire.WriteAsync(Join(status == TransactionStatus.FailedTransaction ? Error() : Query(i),
                    Ready((char)status)), fragment);
                if (status == TransactionStatus.FailedTransaction)
                {
                    var error = await Assert.ThrowsAsync<MpgsqlServerException>(() => batch.Completion.WaitAsync(TestTimeout, token));
                    Assert.Equal(status, error.TransactionStatus);
                }
                else
                {
                    await batch.Completion.WaitAsync(TestTimeout, token);
                }
                Assert.Equal(status, wire.Session.LastTransactionStatus);
                Assert.True(wire.Session.IsHealthy);
                Assert.Equal(status == TransactionStatus.Idle, wire.Session.IsIdleAndHealthy);
            }
            finally
            {
                try { await batch.DisposeAsync(); }
                catch (MpgsqlServerException) when (status == TransactionStatus.FailedTransaction) { }
            }
            Assert.Equal(0, wire.Session.BufferedRowBytes);
        }
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task StopIsVisibleBeforeItsCompletionIsObservedAndNeverReturnsHealthy(bool abort)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var wire = new ScriptedSession(lifetime: lifetime.Token);
        Assert.True(wire.Session.IsIdleAndHealthy);
        var cause = new IOException("health failure");
        if (abort)
        {
            wire.Session.Abort(cause);
        }
        else
        {
            lifetime.Cancel();
        }
        Assert.False(wire.Session.IsHealthy);
        Assert.False(wire.Session.IsIdleAndHealthy);
        if (abort)
        {
            Assert.Same(cause, await Assert.ThrowsAsync<IOException>(() => wire.Session.Completion.WaitAsync(TestTimeout,
                TestContext.Current.CancellationToken)));
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wire.Session.Completion.WaitAsync(TestTimeout,
                TestContext.Current.CancellationToken));
        }
        await wire.Session.DisposeAsync();
        for (var i = 0; i < 64; i++)
        {
            Assert.False(wire.Session.IsHealthy);
            Assert.False(wire.Session.IsIdleAndHealthy);
            Assert.Equal(TransactionStatus.Idle, wire.Session.LastTransactionStatus);
        }
    }

    [Theory, InlineData('T'), InlineData('E')]
    public async Task ExclusiveNonIdleSessionAllowsItsOwnerButIsRetiredOnReturn(char status)
    {
        var token = TestContext.Current.CancellationToken;
        await using var wire = new ScriptedSession();
        await using var replacement = new ScriptedSession();
        var factories = 0;
        await using var source = new MpgsqlDataSource(_ => ValueTask.FromResult(
                Interlocked.Increment(ref factories) == 1 ? wire.Session : replacement.Session),
            (_, _) => ValueTask.CompletedTask, new MpgsqlDataSourceOptions {MaxConnections = 1});
        var connection = await source.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand("select state");
        var execution = command.ExecuteScalarAsync<long>(token).AsTask();
        var tags = new List<char>();
        do { tags.AddRange(Tags(await wire.ReadOutputAsync())); } while (!tags.Contains('S'));
        Assert.Equal("PBDES", new string([.. tags]));
        await wire.WriteAsync(Join(status == 'E' ? Error() : Query(42), Ready(status)), 1);
        if (status == 'E')
        {
            var error = await Assert.ThrowsAsync<MpgsqlPostgresException>(() => execution.WaitAsync(TestTimeout, token));
            Assert.Equal(TransactionStatus.FailedTransaction, error.TransactionStatus);
        }
        else
        {
            Assert.Equal(42, (await execution.WaitAsync(TestTimeout, token)).Value);
        }
        Assert.True(wire.Session.IsHealthy);
        Assert.False(wire.Session.IsIdleAndHealthy);
        // The exclusive owner can still construct the next operation inside its transaction.
        await using var next = connection.CreateCommand("rollback");
        await connection.DisposeAsync();
        Assert.False(wire.Session.IsHealthy);
        Assert.True(wire.Session.Completion.IsCompleted);
        Assert.False(wire.HasOutput()); // Returning it did not send reset/rollback SQL.
        await using var fresh = await source.OpenConnectionAsync(token);
        Assert.Equal(2, factories);
        Assert.Same(replacement.Session, fresh.Session);
        Assert.True(replacement.Session.IsIdleAndHealthy);
        Assert.Equal(0, wire.Session.BufferedRowBytes);
    }
}