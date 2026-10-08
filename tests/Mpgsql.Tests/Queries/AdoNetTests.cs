using System.Buffers;
using System.Data;
using System.Data.Common;
using Mpgsql.Tests.Protocol;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class AdoNetTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static MpgsqlDataSource Source(ScriptedSession wire)
    {
        return new MpgsqlDataSource(_ => ValueTask.FromResult(wire.Session), (_, _) => ValueTask.CompletedTask);
    }
    private static async Task<byte[]> Sync(ScriptedSession wire)
    {
        var bytes = new List<byte>();
        do { bytes.AddRange(await wire.ReadOutputAsync()); } while (!Tags([.. bytes]).Contains('S'));
        return [.. bytes];
    }

    [Fact]
    public async Task DbCommandReusesTypedParametersAndReturnsStandardNullSemantics()
    {
        await using var wire = new ScriptedSession();
        await using DbDataSource source = Source(wire);
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "select $1";
        var parameter = new MpgsqlParameter<long>(TypeOid.Int64, 7);
        parameter.ParameterName = "value";
        command.Parameters.Add(parameter);
        for (var i = 0; i < 3; i++)
        {
            parameter.TypedValue = i + 7;
            var scalar = command.ExecuteScalarAsync(Token);
            Assert.Equal("PBDES", new string(Tags(await Sync(wire))));
            Assert.Throws<InvalidOperationException>(() => parameter.TypedValue = 99);
            Assert.Throws<InvalidOperationException>(() => command.Connection = null);
            await wire.WriteAsync(i == 0 ? Join(Query(7), Ready()) : i == 1 ? Join(Begin(20), Row((byte[]?)null), Command(), Ready()) : Join(Begin(20), Command("SELECT 0"), Ready()));
            var result = await scalar.WaitAsync(TestTimeout, Token);
            if (i == 0)
            {
                Assert.Equal(7L, result);
            }
            else if (i == 1)
            {
                Assert.Same(DBNull.Value, result);
            }
            else
            {
                Assert.Null(result);
            }
        }
        Assert.Same(parameter, command.Parameters["value"]);
    }

    [Fact]
    public async Task DbReaderPrefetchesHasRowsAndPreservesMetadataAndBorrowedPayload()
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire);
        await using DbConnection connection = await source.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "select values";
        var opening = command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, Token);
        await Sync(wire);
        await wire.WriteAsync(Join(Begin(20, 25), Row(Int64(42), "hello"u8.ToArray()), Command(), Ready()), 1);
        await using var reader = await opening.WaitAsync(TestTimeout, Token);
        Assert.True(reader.HasRows);
        Assert.Equal(2, reader.FieldCount);
        Assert.Equal(typeof(long), reader.GetFieldType(0));
        Assert.Equal("int8", reader.GetDataTypeName(0));
        Assert.Throws<InvalidOperationException>(() => reader.GetValue(0));
        Assert.True(await reader.ReadAsync(Token));
        Assert.Equal(42L, reader[0]);
        Assert.Equal("hello", reader.GetString(1));
        Assert.Equal(42L, reader.GetFieldValue<object>(0));
        Assert.Equal(Int64(42), ((MpgsqlDataReader)reader).GetRawValue(0)!.Value.ToArray());
        var values = new object[3];
        Assert.Equal(2, reader.GetValues(values));
        Assert.False(await reader.ReadAsync(Token));
        Assert.True(reader.HasRows);
        Assert.False(await reader.NextResultAsync(Token));
        Assert.Equal(-1, reader.RecordsAffected);
        Assert.False(reader.IsClosed);
        Assert.False(await reader.ReadAsync(Token));
        Assert.False(await reader.NextResultAsync(Token));
        Assert.Equal(0, wire.Session.BufferedRowBytes);
    }

    [Fact]
    public async Task PrepareAndUnprepareHaveDistinctCompleteWirePayloadsAndFreezeOnlySignature()
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire);
        await using var connection = await source.OpenConnectionAsync(Token);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = "select $1";
        var parameter = MpgsqlParameter.Int64(42L);
        command.Parameters.Add(parameter);
        var prepare = command.PrepareAsync(Token);
        Assert.Equal(TestWire.Bytes("50 00000020 6d706773716c5f70735f3100 73656c65637420243100 0001 00000014 53 00000004"), await Sync(wire));
        await wire.WriteAsync(Join(Packet('1'), Ready()));
        await prepare.WaitAsync(TestTimeout, Token);
        Assert.Throws<InvalidOperationException>(() => command.CommandText = "select 2");
        Assert.Throws<InvalidOperationException>(() => parameter.PostgresTypeOid = 23);
        parameter.Value = 43L;
        var scalar = command.ExecuteScalarAsync(Token);
        Assert.Equal(TestWire.Bytes("42 00000027 00 6d706773716c5f70735f3100 0001 0001 0001 00000008 000000000000002b 0001 0001 44 00000006 50 00 45 00000009 00 00000000 53 00000004"), await Sync(wire));
        await wire.WriteAsync(Join(Packet('2'), Description(20), Row(Int64(43)), Command(), Ready()));
        Assert.Equal(43L, await scalar.WaitAsync(TestTimeout, Token));
        var close = ((MpgsqlCommand)command).UnprepareAsync(Token).AsTask();
        Assert.Equal(TestWire.Bytes("43 00000011 53 6d706773716c5f70735f3100 53 00000004"), await Sync(wire));
        await wire.WriteAsync(Join(Packet('3'), Ready()));
        await close.WaitAsync(TestTimeout, Token);
        command.CommandText = "select changed";
    }

    [Fact]
    public async Task PrepareWithoutCommandTimeoutCanExceedRecoveryTimeout()
    {
        await using var wire = new ScriptedSession();
        await using var source = new MpgsqlDataSource(_ => ValueTask.FromResult(wire.Session),
            (_, _) => ValueTask.CompletedTask, new MpgsqlDataSourceOptions {RecoveryTimeout = TimeSpan.FromMilliseconds(100)});
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand("select 42::bigint");
        var preparing = command.PrepareAsync(Token);
        await Sync(wire);
        await Task.Delay(250, Token);
        Assert.False(preparing.IsCompleted);
        Assert.True(wire.Session.IsHealthy);
        await wire.WriteAsync(Join(Packet('1'), Ready()));
        await preparing.WaitAsync(TestTimeout, Token);
        var closing = command.UnprepareAsync(Token).AsTask();
        await Sync(wire);
        await wire.WriteAsync(Join(Packet('3'), Ready()));
        await closing.WaitAsync(TestTimeout, Token);
    }

    [Fact]
    public async Task AsyncCloseBoundsRecoveryOfAlreadyWaitingPreparation()
    {
        await using var wire = new ScriptedSession();
        await using var source = new MpgsqlDataSource(_ => ValueTask.FromResult(wire.Session),
            (_, _) => ValueTask.CompletedTask, new MpgsqlDataSourceOptions {RecoveryTimeout = TimeSpan.FromMilliseconds(100)});
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand("select delayed");
        var preparing = command.PrepareAsync(Token);
        await Sync(wire);
        var closing = connection.CloseAsync();
        await Assert.ThrowsAsync<MpgsqlException>(() => preparing.WaitAsync(TestTimeout, Token));
        try { await closing.WaitAsync(TestTimeout, Token); }
        catch (MpgsqlException) { }
        Assert.False(wire.Session.IsHealthy);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task PrepareServerErrorWithoutReadyClosesSessionAtRecoveryDeadline()
    {
        await using var wire = new ScriptedSession();
        await using var source = new MpgsqlDataSource(_ => ValueTask.FromResult(wire.Session),
            (_, _) => ValueTask.CompletedTask, new MpgsqlDataSourceOptions {RecoveryTimeout = TimeSpan.FromMilliseconds(100)});
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand("select missing");
        var preparing = command.PrepareAsync(Token);
        await Sync(wire);
        await wire.WriteAsync(Error("42P01"));
        await Assert.ThrowsAnyAsync<MpgsqlException>(() => preparing.WaitAsync(TestTimeout, Token));
        Assert.False(wire.Session.IsHealthy);
    }

    [Fact]
    public async Task UnresponsiveCancellationChannelClosesSessionAtRecoveryDeadline()
    {
        await using var wire = new ScriptedSession();
        var cancelStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var source = new MpgsqlDataSource(_ => ValueTask.FromResult(wire.Session),
            (_, _) =>
            {
                cancelStarted.TrySetResult();
                return new ValueTask(cancelFinished.Task);
            },
            new MpgsqlDataSourceOptions {RecoveryTimeout = TimeSpan.FromMilliseconds(200)});
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand("select delayed");
        try
        {
            var executing = command.ExecuteScalarAsync<long>(Token).AsTask();
            await Sync(wire);
            command.Cancel();
            await cancelStarted.Task.WaitAsync(TestTimeout, Token);
            await wire.WriteAsync(Join(Error("57014"), Ready()));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executing.WaitAsync(TestTimeout, Token));
            Assert.False(wire.Session.IsHealthy);
        }
        finally { cancelFinished.TrySetResult(); }
    }

    [Fact]
    public async Task CancelBeforeReadingPrefetchedRowWaitsForRecoveryAndReleasesConnection()
    {
        await using var wire = new ScriptedSession();
        var cancelSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var source = new MpgsqlDataSource(_ => ValueTask.FromResult(wire.Session),
            (_, _) =>
            {
                cancelSent.TrySetResult();
                return ValueTask.CompletedTask;
            });
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand("select delayed");
        var opening = command.ExecuteReaderAsync(Token);
        await Sync(wire);
        await wire.WriteAsync(Join(Begin(20), Row(Int64(42))));
        await using var reader = await opening.WaitAsync(TestTimeout, Token);
        Assert.True(reader.HasRows);
        command.Cancel();
        await cancelSent.Task.WaitAsync(TestTimeout, Token);
        var reading = reader.ReadAsync(Token);
        Assert.False(reading.IsCompleted);
        await wire.WriteAsync(Join(Error("57014"), Ready()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading.WaitAsync(TestTimeout, Token));
        command.CommandText = "select next";
        var next = command.ExecuteScalarAsync<long>(Token).AsTask();
        await Sync(wire);
        await wire.WriteAsync(Join(Query(43), Ready()));
        Assert.Equal(43, (await next.WaitAsync(TestTimeout, Token)).Value);
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Fact]
    public void FactoryCommandAndBatchInheritTimeoutUnlessExplicitlyAssigned()
    {
        using var first = new MpgsqlConnection("Username=test;Command Timeout=7");
        using var second = new MpgsqlConnection("Username=test;Command Timeout=9");
        using var command = MpgsqlFactory.Instance.CreateCommand();
        using var batch = MpgsqlFactory.Instance.CreateBatch();
        command.Connection = first;
        batch.Connection = first;
        Assert.Equal(7, command.CommandTimeout);
        Assert.Equal(7, batch.Timeout);
        command.Connection = second;
        batch.Connection = second;
        Assert.Equal(9, command.CommandTimeout);
        Assert.Equal(9, batch.Timeout);
        command.CommandTimeout = 0;
        batch.Timeout = 0;
        command.Connection = first;
        batch.Connection = first;
        Assert.Equal(0, command.CommandTimeout);
        Assert.Equal(0, batch.Timeout);
    }

    [Fact]
    public async Task CancelledPrepareRetainsConfirmedParseForExplicitUnprepare()
    {
        await using var wire = new ScriptedSession();
        var cancelSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var source = new MpgsqlDataSource(_ => ValueTask.FromResult(wire.Session),
            (_, _) =>
            {
                cancelSent.TrySetResult();
                return ValueTask.CompletedTask;
            });
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand("select $1");
        command.Parameters.Add(MpgsqlParameter.Int64(42));
        using var cancellation = new CancellationTokenSource();
        var preparing = command.PrepareAsync(cancellation.Token);
        Assert.Equal("PS", new string(Tags(await Sync(wire))));
        await wire.WriteAsync(Packet('1'));
        cancellation.Cancel();
        await cancelSent.Task.WaitAsync(TestTimeout, Token);
        await wire.WriteAsync(Ready());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparing.WaitAsync(TestTimeout, Token));
        Assert.Throws<InvalidOperationException>(() => command.CommandText = "select changed");
        var closing = command.UnprepareAsync(Token).AsTask();
        Assert.Equal(TestWire.Bytes("43 00000011 53 6d706773716c5f70735f3100 53 00000004"), await Sync(wire));
        await wire.WriteAsync(Join(Packet('3'), Ready()));
        await closing.WaitAsync(TestTimeout, Token);
        command.CommandText = "select changed";
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Theory, InlineData(IsolationLevel.ReadCommitted), InlineData(IsolationLevel.ReadUncommitted), InlineData(IsolationLevel.RepeatableRead), InlineData(IsolationLevel.Serializable)]
    public async Task DbTransactionsBeginAndRollbackAsynchronously(IsolationLevel isolation)
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire);
        await using DbConnection connection = await source.OpenConnectionAsync(Token);
        var beginning = connection.BeginTransactionAsync(isolation, Token).AsTask();
        Assert.Equal("PBDES", new string(Tags(await Sync(wire))));
        await wire.WriteAsync(Join(Packet('1'), Packet('2'), Packet('n'), Command("BEGIN"), Ready('T')));
        var transaction = await beginning.WaitAsync(TestTimeout, Token);
        Assert.Equal(isolation, transaction.IsolationLevel);
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.BeginTransactionAsync(Token).AsTask());
        var disposing = transaction.DisposeAsync().AsTask();
        await Sync(wire);
        await wire.WriteAsync(Join(Packet('1'), Packet('2'), Packet('n'), Command("ROLLBACK"), Ready()));
        await disposing.WaitAsync(TestTimeout, Token);
        Assert.Null(transaction.Connection);
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Fact]
    public async Task StandardNonQueryChecksIntOverflowAfterProtocolCompletion()
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire);
        await using var connection = await source.OpenConnectionAsync(Token);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = "update large";
        var pending = command.ExecuteNonQueryAsync(Token);
        await Sync(wire);
        await wire.WriteAsync(Join(Packet('1'), Packet('2'), Packet('n'), Command("UPDATE 2147483648"), Ready()));
        await Assert.ThrowsAsync<OverflowException>(() => pending.WaitAsync(TestTimeout, Token));
        command.CommandText = "reusable after overflow";
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Fact]
    public async Task DbBatchFreezesEveryCommandAndAttributesAffectedRows()
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire);
        await using DbConnection connection = await source.OpenConnectionAsync(Token);
        await using var batch = connection.CreateBatch();
        var first = batch.CreateBatchCommand();
        first.CommandText = "update first";
        batch.BatchCommands.Add(first);
        var second = batch.CreateBatchCommand();
        second.CommandText = "update second";
        batch.BatchCommands.Add(second);
        var pending = batch.ExecuteNonQueryAsync(Token);
        Assert.Equal("PBDEPBDES", new string(Tags(await Sync(wire))));
        Assert.Throws<InvalidOperationException>(() => second.CommandText = "changed");
        await wire.WriteAsync(Join(Packet('1'), Packet('2'), Packet('n'), Command("UPDATE 2"), Packet('1'), Packet('2'), Packet('n'), Command("DELETE 3"), Ready()));
        Assert.Equal(5, await pending.WaitAsync(TestTimeout, Token));
        Assert.Equal(2, first.RecordsAffected);
        Assert.Equal(3, second.RecordsAffected);
        second.CommandText = "reusable";
    }

    [Fact]
    public async Task AlreadyCancelledReadTokenCancelsPublishedExecutionAndPreservesToken()
    {
        await using var wire = new ScriptedSession();
        var cancels = 0;
        await using var source = new MpgsqlDataSource(_ => ValueTask.FromResult(wire.Session), (_, _) =>
        {
            Interlocked.Increment(ref cancels);
            return ValueTask.CompletedTask;
        });
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand("select delayed rows");
        var opening = command.ExecuteReaderAsync(Token);
        await Sync(wire);
        await wire.WriteAsync(Join(Begin(20), Row(Int64(1))));
        await using var reader = await opening.WaitAsync(TestTimeout, Token);
        using var movement = new CancellationTokenSource();
        movement.Cancel();
        var reading = reader.ReadAsync(movement.Token);
        await wire.WriteAsync(Join(Error("57014"), Ready()));
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading.WaitAsync(TestTimeout, Token));
        Assert.Equal(movement.Token, error.CancellationToken);
        Assert.True(wire.Session.IsIdleAndHealthy);
        command.CommandText = "recovered";
    }

    [Fact]
    public async Task FirstCancellationReasonSurvivesLaterCommandTimeoutDuringRecovery()
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire);
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand("select delayed");
        command.CommandTimeout = 1;
        var opening = command.ExecuteReaderAsync(Token);
        await Sync(wire);
        command.Cancel();
        await Task.Delay(1100, Token);
        await wire.WriteAsync(Join(Error("57014"), Ready()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TestTimeout, Token));
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Fact]
    public async Task FailedBatchPrepareRetainsOnlyConfirmedHandlesForExplicitUnprepare()
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire);
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var batch = connection.CreateBatch();
        batch.BatchCommands.Add(new MpgsqlBatchCommand("select good"));
        batch.BatchCommands.Add(new MpgsqlBatchCommand("syntax error"));
        batch.BatchCommands.Add(new MpgsqlBatchCommand("select skipped"));
        var preparing = batch.PrepareAsync(Token);
        Assert.Equal("PPPS", new string(Tags(await Sync(wire))));
        await wire.WriteAsync(Join(Packet('1'), Error("42601"), Ready()));
        await Assert.ThrowsAsync<MpgsqlPostgresException>(() => preparing.WaitAsync(TestTimeout, Token));
        Assert.NotNull(batch.BatchCommands[0].Statement);
        Assert.Null(batch.BatchCommands[1].Statement);
        Assert.Null(batch.BatchCommands[2].Statement);
        var unpreparing = batch.UnprepareAsync(Token).AsTask();
        Assert.Equal("CS", new string(Tags(await Sync(wire))));
        await wire.WriteAsync(Join(Packet('3'), Ready()));
        await unpreparing.WaitAsync(TestTimeout, Token);
        Assert.Equal(0, wire.Session.TrackedStatementCount);
        batch.BatchCommands[0].CommandText = "changed";
    }

    [Fact]
    public async Task AsyncReaderCloseKeepsAffectedRowsOfDiscardedBatchCommands()
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire);
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var batch = connection.CreateBatch();
        batch.BatchCommands.Add(new MpgsqlBatchCommand("select rows"));
        batch.BatchCommands.Add(new MpgsqlBatchCommand("update later"));
        var opening = batch.ExecuteReaderAsync(Token);
        await Sync(wire);
        await wire.WriteAsync(Join(Begin(20), Row(Int64(1))));
        var reader = await opening.WaitAsync(TestTimeout, Token);
        var closing = reader.DisposeAsync().AsTask();
        await wire.WriteAsync(Join(Row(Int64(2)), Command("SELECT 2"), Packet('1'), Packet('2'), Packet('n'), Command("UPDATE 7"), Ready()));
        await closing.WaitAsync(TestTimeout, Token);
        Assert.Equal(7, reader.RecordsAffected);
        Assert.Equal(7, batch.BatchCommands[1].RecordsAffected);
        Assert.True(reader.IsClosed);
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Fact]
    public void FactoryAndConnectionDefaultsRejectUnsupportedOperations()
    {
        DbProviderFactory factory = MpgsqlFactory.Instance;
        using var connection = factory.CreateConnection()!;
        using var source = factory.CreateDataSource("Username=test");
        Assert.IsType<MpgsqlDataSource>(source);
        Assert.Throws<NotSupportedException>(connection.Open);
        using var command = factory.CreateCommand()!;
        Assert.Throws<NotSupportedException>(() => command.ExecuteScalar());
        Assert.Throws<NotSupportedException>(() => command.CommandType = CommandType.StoredProcedure);
        var builder = new MpgsqlConnectionStringBuilder("Username=test");
        Assert.Equal("localhost", builder.Host);
        Assert.Equal("test", builder.Database);
        Assert.Equal(MpgsqlSslMode.VerifyFull, builder.SslMode);
        Assert.Equal(15, builder.Timeout);
        Assert.Equal(0, builder.CommandTimeout);
        Assert.Equal(10, builder.MaxPoolSize);
        Assert.Throws<ArgumentException>(() => builder.ConnectionString = "Unknown=true");
        DbConnectionStringBuilder baseBuilder = builder;
        Assert.Throws<ArgumentException>(() => baseBuilder["Unknown"] = "x");
    }
}