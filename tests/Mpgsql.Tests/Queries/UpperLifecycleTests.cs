using System.IO.Pipelines;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class UpperLifecycleTests
{
    private static async Task Sync(ScriptedSession wire)
    {
        while (!Tags(await wire.ReadOutputAsync())
                   .Contains('S')) { }
    }

    [Fact]
    public async Task DuplicateFactorySessionIsRejectedWithoutClosingItsExistingLease()
    {
        await using var wire = new ScriptedSession();
        await using var source = new MpgsqlDataSource
        (
            _ => ValueTask.FromResult(wire.Session),
            (_, _) => ValueTask.CompletedTask, new MpgsqlDataSourceOptions
            {
                MaxConnections = 2
            }
        );
        await using var connection = await source.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>
        (() => source
            .OpenConnectionAsync(TestContext.Current.CancellationToken)
            .AsTask()
        );
        Assert.False(wire.Session.Completion.IsCompleted);
        await using var command = connection.CreateCommand("select 3::bigint");
        var result = command
            .ExecuteScalarAsync<long>(TestContext.Current.CancellationToken)
            .AsTask();
        await Sync(wire);
        await wire.WriteAsync(Join(Query(3), Ready()));
        Assert.Equal(3, (await result).Value);
    }

    [Fact]
    public async Task OwnedStreamAdaptersCanFinishBeforeSessionDisposal()
    {
        using var stream = new MemoryStream();
        var session = new MpgsqlMessageSession(PipeReader.Create(stream), PipeWriter.Create(stream));
        await Assert.ThrowsAsync<EndOfStreamException>(() => session.Completion.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
        await session.DisposeAsync();
        await session.DisposeAsync();
    }

    [Fact]
    public async Task OwnerDisposalCanDrainWhileReaderMovementIsWaiting()
    {
        await using var wire = new ScriptedSession();
        await using var source = new MpgsqlDataSource
        (
            _ => ValueTask.FromResult(wire.Session),
            (_, _) => ValueTask.CompletedTask, new MpgsqlDataSourceOptions
            {
                MaxConnections = 1
            }
        );
        await using var connection = await source.OpenConnectionAsync(TestContext.Current.CancellationToken);
        var command = connection.CreateCommand("select delayed");
        var opening = command
            .ExecuteReaderValueTaskAsync(cancellationToken: TestContext.Current.CancellationToken)
            .AsTask();
        await Sync(wire);
        var initialWrite = wire.WriteAsync(Join(Begin(20), Row(Int64(0))));
        var reader = await opening;
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        var moving = reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.False(moving.IsCompleted);
        var disposing = command
            .DisposeAsync()
            .AsTask();
        Assert.False(disposing.IsCompleted);
        await initialWrite.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        await wire.WriteAsync(Join(Row(Int64(1)), Command(), Ready()));
        await disposing.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => moving.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
        await reader.DisposeAsync();
        await using var next = connection.CreateCommand("select 2::bigint");
        var result = next
            .ExecuteScalarAsync<long>(TestContext.Current.CancellationToken)
            .AsTask();
        await Sync(wire);
        await wire.WriteAsync(Join(Query(2), Ready()));
        Assert.Equal(2, (await result).Value);
    }

    [Fact]
    public async Task FactoryConcurrencyIsBoundedAndIdleLeasesAreReused()
    {
        var firstFactory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueFactory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var a = new ScriptedSession();
        await using var b = new ScriptedSession();
        var calls = 0;
        await using var source = new MpgsqlDataSource
        (
            async token =>
            {
                var number = Interlocked.Increment(ref calls);
                firstFactory.TrySetResult();
                await continueFactory.Task.WaitAsync(token);
                return number == 1 ? a.Session : b.Session;
            }, (_, _) => ValueTask.CompletedTask, new MpgsqlDataSourceOptions
            {
                MaxConnections = 2
            }
        );
        var one = source
            .OpenConnectionAsync(TestContext.Current.CancellationToken)
            .AsTask();
        await firstFactory.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        var two = source
            .OpenConnectionAsync(TestContext.Current.CancellationToken)
            .AsTask();
        var three = source
            .OpenConnectionAsync(TestContext.Current.CancellationToken)
            .AsTask();
        Assert.Equal(2, calls);
        Assert.False(three.IsCompleted);
        continueFactory.TrySetResult();
        var c1 = await one;
        var c2 = await two;
        await c1.DisposeAsync();
        var c3 = await three.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.Equal(2, calls);
        Assert.False(a.HasOutput());
        Assert.False(b.HasOutput());
        await c2.DisposeAsync();
        await c3.DisposeAsync();
    }

    [Fact]
    public async Task FactoryFailureReleasesCreationCapacity()
    {
        await using var wire = new ScriptedSession();
        var calls = 0;
        await using var source = new MpgsqlDataSource
        (
            _ => ++calls == 1
                ? ValueTask.FromException<MpgsqlMessageSession>(new IOException("startup failed"))
                : ValueTask.FromResult(wire.Session), (_, _) => ValueTask.CompletedTask, new MpgsqlDataSourceOptions
            {
                MaxConnections = 1
            }
        );
        await Assert.ThrowsAsync<MpgsqlException>
        (() => source
            .OpenConnectionAsync(TestContext.Current.CancellationToken)
            .AsTask()
        );
        await using var connection = await source.OpenConnectionAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, calls);
    }

    [Theory, InlineData('T'), InlineData('E')]
    public async Task ReturningNonIdleTransportClosesItWithoutRollback(char status)
    {
        await using var a = new ScriptedSession();
        await using var b = new ScriptedSession();
        var calls = 0;
        await using var source = new MpgsqlDataSource
        (
            _ => ValueTask.FromResult(++calls == 1 ? a.Session : b.Session),
            (_, _) => ValueTask.CompletedTask, new MpgsqlDataSourceOptions
            {
                MaxConnections = 1
            }
        );
        var connection = await source.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand("begin");
        var pending = command
            .ExecuteNonQuery64Async(TestContext.Current.CancellationToken)
            .AsTask();
        await Sync(a);
        await a.WriteAsync(Join(Packet('1'), Packet('2'), Packet('n'), Command("BEGIN"), Ready(status)));
        Assert.Equal(-1, await pending);
        await connection.DisposeAsync();
        Assert.True(a.Session.Completion.IsCompleted);
        Assert.False(a.HasOutput());
        await using var next = await source.OpenConnectionAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task RecoveryTimeoutRetiresTransportAndReleasesBorrowedInputs()
    {
        await using var a = new ScriptedSession(true);
        await using var b = new ScriptedSession();
        var calls = 0;
        await using var source = new MpgsqlDataSource
        (
            _ => ValueTask.FromResult(++calls == 1 ? a.Session : b.Session),
            (_, _) => ValueTask.CompletedTask, new MpgsqlDataSourceOptions
            {
                MaxConnections = 1,
                RecoveryTimeout = TimeSpan.FromMilliseconds(100)
            }
        );
        var connection = await source.OpenConnectionAsync(TestContext.Current.CancellationToken);
        var batch = connection.CreateBatch();
        batch.BatchCommands.Add(new MpgsqlBatchCommand("select first"));
        long[] payload = [1, 2, 3];
        var later = new MpgsqlBatchCommand("select $1");
        later.Parameters.Add(MpgsqlParameterValue.Int64Array(payload));
        batch.BatchCommands.Add(later);
        var opening = batch
            .ExecuteReaderValueTaskAsync(cancellationToken: TestContext.Current.CancellationToken)
            .AsTask();
        var held = await a.Outgoing.Reader.ReadAsync(TestContext.Current.CancellationToken);
        var initialWrite = a.WriteAsync(Join(Begin(20), Row(Int64(0))));
        var reader = await opening;
        var closing = reader.DisposeAsync().AsTask();
        await initialWrite.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<MpgsqlException>
        (() => closing.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
        Assert.IsType<TimeoutException>(error.InnerException);
        payload[0] = 99;
        a.Outgoing.Reader.AdvanceTo(held.Buffer.End);
        Assert.True(a.Session.Completion.IsCompleted);
        // The error was already observed. All owners still release their descriptors.
        await batch.DisposeAsync();
        Assert.Empty(later.Parameters);
        await connection.DisposeAsync();
        await using var next = await source.OpenConnectionAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CancelCallbackFailureRetiresItsTransport()
    {
        await using var a = new ScriptedSession();
        await using var b = new ScriptedSession();
        var calls = 0;
        await using var source = new MpgsqlDataSource
        (
            _ => ValueTask.FromResult(++calls == 1 ? a.Session : b.Session),
            (_, _) => ValueTask.FromException(new IOException("cancel channel failed")), new MpgsqlDataSourceOptions
            {
                MaxConnections = 1
            }
        );
        var connection = await source.OpenConnectionAsync(TestContext.Current.CancellationToken);
        var command = connection.CreateCommand("select slow");
        using var request = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var opening = command
            .ExecuteReaderValueTaskAsync(cancellationToken: request.Token)
            .AsTask();
        await Sync(a);
        request.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
        Assert.True(a.Session.Completion.IsCompleted);
        await command.DisposeAsync();
        await connection.DisposeAsync();
        await using var next = await source.OpenConnectionAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CancellationAfterCompletionCannotCancelNextCommand()
    {
        await using var wire = new ScriptedSession();
        var cancels = 0;
        await using var source = new MpgsqlDataSource
        (
            _ => ValueTask.FromResult(wire.Session),
            (_, _) =>
            {
                cancels++;
                return ValueTask.CompletedTask;
            }, new MpgsqlDataSourceOptions
            {
                MaxConnections = 1
            }
        );
        await using var connection = await source.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var first = connection.CreateCommand("select 1::bigint");
        using var request = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var result = first
            .ExecuteScalarAsync<long>(request.Token)
            .AsTask();
        await Sync(wire);
        await wire.WriteAsync(Join(Query(1), Ready()));
        Assert.Equal(1, (await result).Value);
        await using var second = connection.CreateCommand("select 2::bigint");
        var next = second
            .ExecuteScalarAsync<long>(TestContext.Current.CancellationToken)
            .AsTask();
        await Sync(wire);
        request.Cancel();
        await wire.WriteAsync(Join(Query(2), Ready()));
        Assert.Equal(2, (await next).Value);
        Assert.Equal(0, cancels);
    }
}
