using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class ExecutionEntryTests
{
    [Theory, InlineData(false), InlineData(true)]
    public async Task DataSourceEntryFailureIsCapturedWithoutOpeningATransport(bool cancelled)
    {
        var opened = 0;
        await using var source = new MpgsqlMultiplexingDataSource
        (_ =>
            {
                opened++;
                return ValueTask.FromException<MpgsqlMessageSession>(new InvalidOperationException("Unexpected factory call."));
            }
        );
        using var request = new CancellationTokenSource();
        if (cancelled)
        {
            request.Cancel();
        }
        // Calling the async API itself must not throw; it returns the failed operation.
        var operation = source.ExecuteReaderAsync
        (
            cancelled ? "select 1" : null!,
            cancellationToken: request.Token
        );
        var task = operation.AsTask();
        if (cancelled)
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Assert.Equal(request.Token, error.CancellationToken);
            Assert.True(task.IsCanceled);
        }
        else
        {
            await Assert.ThrowsAsync<ArgumentNullException>(() => task);
            Assert.True(task.IsFaulted);
        }
        Assert.Equal(0, opened);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task DisposedCommandOrBatchReturnsAFailedOperationWithoutWriting(bool batchPath)
    {
        await using var wire = new ScriptedSession();
        await using var source = new MpgsqlDataSource
        (
            _ => ValueTask.FromResult(wire.Session),
            (_, _) => ValueTask.CompletedTask
        );
        await using var connection = await source.OpenConnectionAsync(TestContext.Current.CancellationToken);
        ValueTask<MpgsqlDataReader> operation;
        if (batchPath)
        {
            await using var batch = connection.CreateBatch();
            await batch.DisposeAsync();
            operation = batch.ExecuteReaderValueTaskAsync(cancellationToken: TestContext.Current.CancellationToken);
        }
        else
        {
            await using var command = connection.CreateCommand("select 1");
            await command.DisposeAsync();
            operation = command.ExecuteReaderValueTaskAsync(cancellationToken: TestContext.Current.CancellationToken);
        }
        await Assert.ThrowsAsync<ObjectDisposedException>(() => operation.AsTask());
        Assert.False(wire.HasOutput());
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Fact]
    public async Task UncancelableAdmissionIsStillReleasedWhenTheSourceIsDisposed()
    {
        await using var wire = new ScriptedSession();
        await using var source = new MpgsqlMultiplexingDataSource
        (
            _ => ValueTask.FromResult(wire.Session), new MpgsqlMultiplexingOptions
            {
                MaxConnections = 1,
                MaxInFlightPerConnection = 1
            }
        );
        await using var held = await MultiplexingLease.HoldAsync(wire, source, TestContext.Current.CancellationToken);
#pragma warning disable xUnit1051 // This case deliberately exercises the uncancelable admission path.
        var waiting = source
            .ExecuteReaderAsync("select queued", cancellationToken: CancellationToken.None)
            .AsTask();
#pragma warning restore xUnit1051
        Assert.False(waiting.IsCompleted);
        Assert.False(wire.HasOutput());
        await source.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>
        (() => waiting.WaitAsync
            (
                TestTimeout,
                TestContext.Current.CancellationToken
            )
        );
    }
}