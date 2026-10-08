using System.Buffers;
using Mpgsql.Internal;
using Mpgsql.Protocol;
using Mpgsql.Tests.Protocol;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class PreparedStatementTests
{
    private static Task Wait(Task task) => task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
    private static byte[] Execution(long value) => Join(Packet('2'), Description(20), Row(Int64(value)), Command());

    [Fact]
    public async Task CreationCopiesTypesAndDoesNotWrite()
    {
        await using var wire = new ScriptedSession();
        uint[] types = [20];
        var statement = wire.Session.CreatePreparedStatement("select $1", types);
        types[0] = 1016;
        Assert.Equal("mpgsql_ps_1", statement.Name);
        Assert.Equal("select $1", statement.Sql);
        Assert.Equal(new uint[] {20}, statement.ParameterTypes.ToArray());
        Assert.Equal("mpgsql_ps_2", wire.Session.CreatePreparedStatement("select 1").Name);
        Assert.False(statement.Prepared.IsCompleted);
        Assert.False(wire.HasOutput());
        Assert.Throws<ArgumentException>(() => wire.Session.CreatePreparedStatement("select $1", new uint[] {0}));
        Assert.Throws<ArgumentNullException>(() => wire.Session.CreatePreparedStatement(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => wire.Session.CreatePreparedStatement("select 1", new uint[65536]));
        Assert.False(wire.HasOutput());
    }

    [Fact]
    public async Task CompleteNamedPrepareExecuteClosePayloadIncludesTagsLengthsAndFormats()
    {
        await using var wire = new ScriptedSession();
        var statement = wire.Session.CreatePreparedStatement("select $1", new uint[] {20});
        await using var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        Task[] sends =
        [
            batch.SendPrepareAsync(statement).AsTask(),
            batch.SendQueryAsync(statement, new[] {MpgsqlParameter.Int64(42)}).AsTask(),
            batch.SendCloseAsync(statement).AsTask()
        ];
        await batch.SendSyncAsync();
        await Wait(Task.WhenAll(sends));
        Assert.Equal(TestWire.Bytes(
            "50 00000020 6d706773716c5f70735f3100 73656c65637420243100 0001 00000014 " +
            "42 00000027 00 6d706773716c5f70735f3100 0001 0001 0001 00000008 000000000000002a 0001 0001 " +
            "44 00000006 50 00 45 00000009 00 00000000 " +
            "43 00000011 53 6d706773716c5f70735f3100 53 00000004"), await wire.ReadOutputAsync());
        Assert.False(statement.Prepared.IsCompleted);
        await wire.WriteAsync(Join(Packet('1'), Execution(42), Packet('3'), Ready()), fragment: 1);
        await Wait(statement.Prepared);
        await using var reader = await batch.ReadResultsAsync();
        Assert.Equal(0, reader.QueryIndex);
        Assert.True(await reader.ReadAsync());
        Assert.Equal(42, reader.GetInt64(0));
        Assert.False(await reader.NextResultAsync());
    }

    [Fact]
    public async Task PreparationAndUnawaitedExecutionsShareOneBatch()
    {
        await using var wire = new ScriptedSession();
        var statement = wire.Session.CreatePreparedStatement("select $1", new uint[] {20});
        await using var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        Task[] sends =
        [
            batch.SendPrepareAsync(statement).AsTask(),
            batch.SendQueryAsync(statement, new[] {MpgsqlParameter.Int64(11)}).AsTask(),
            batch.SendQueryAsync(statement, new[] {MpgsqlParameter.Int64(22)}).AsTask()
        ];
        await batch.SendSyncAsync();
        await Wait(Task.WhenAll(sends));
        Assert.Equal("PBDEBDES", new string(Tags(await wire.ReadOutputAsync())));
        await wire.WriteAsync(Join(Packet('1'), Execution(11), Execution(22), Ready()));
        await using var reader = await batch.ReadResultsAsync();
        for (int i = 0; i < 2; i++)
        {
            Assert.Equal(i, reader.QueryIndex);
            Assert.True(await reader.ReadAsync());
            Assert.Equal((i + 1) * 11, reader.GetInt64(0));
            Assert.Equal(i == 0, await reader.NextResultAsync());
        }
    }

    [Fact]
    public async Task AdministrativeOperationsDoNotCreateResultsOrChangeQueryIndexes()
    {
        await using var wire = new ScriptedSession();
        var first = wire.Session.CreatePreparedStatement("select $1", new uint[] {20});
        var second = wire.Session.CreatePreparedStatement("select $1", new uint[] {20});
        await using var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await batch.SendQueryAsync("select 1::bigint");
        await batch.SendPrepareAsync(first);
        await batch.SendQueryAsync(first, new[] {MpgsqlParameter.Int64(2)});
        await batch.SendPrepareAsync(second);
        await batch.SendQueryAsync("select 3::bigint");
        await batch.SendCloseAsync(first);
        await batch.SendQueryAsync(second, new[] {MpgsqlParameter.Int64(4)});
        await batch.SendCloseAsync(second);
        await batch.SendSyncAsync();
        await wire.WriteAsync(Join(Query(1), Packet('1'), Execution(2), Packet('1'), Query(3),
            Packet('3'), Execution(4), Packet('3'), Ready()), fragment: 3);
        await using var reader = await batch.ReadResultsAsync();
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(i, reader.QueryIndex);
            Assert.True(await reader.ReadAsync());
            Assert.Equal(i + 1, reader.GetInt64(0));
            Assert.Equal(i < 3, await reader.NextResultAsync());
        }
    }

    [Fact]
    public async Task ParseCompleteAllowsSeveralGroupsInFlightBeforeFirstReady()
    {
        await using var wire = new ScriptedSession();
        var statement = wire.Session.CreatePreparedStatement("select $1", new uint[] {20});
        await using var first = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await first.SendPrepareAsync(statement);
        await first.SendQueryAsync(statement, new[] {MpgsqlParameter.Int64(1)});
        await first.SendSyncAsync();
        await wire.WriteAsync(Packet('1'));
        await Wait(statement.Prepared);
        Assert.False(first.Completion.IsCompleted);
        await using var second = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await second.SendQueryAsync(statement, new[] {MpgsqlParameter.Int64(2)});
        await second.SendSyncAsync();
        Assert.Equal("PBDESBDES", new string(Tags(await wire.ReadOutputAsync())));
        await wire.WriteAsync(Join(Execution(1), Ready(), Execution(2), Ready()));
        await using var secondReader = await second.ReadResultsAsync();
        Assert.True(await secondReader.ReadAsync());
        Assert.Equal(2, secondReader.GetInt64(0));
        Assert.False(await secondReader.NextResultAsync());
        await using var firstReader = await first.ReadResultsAsync();
        Assert.True(await firstReader.ReadAsync());
        Assert.Equal(1, firstReader.GetInt64(0));
        Assert.False(await firstReader.NextResultAsync());
    }

    [Fact]
    public async Task PendingPreparationCannotBeUsedInAnotherBatch()
    {
        await using var wire = new ScriptedSession();
        var statement = wire.Session.CreatePreparedStatement("select $1", new uint[] {20});
        await using var preparing = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await preparing.SendPrepareAsync(statement);
        await preparing.SendSyncAsync();
        Assert.Equal("PS", new string(Tags(await wire.ReadOutputAsync())));
        await using var other = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        Assert.Throws<InvalidOperationException>(() => other.SendQueryAsync(statement, new[] {MpgsqlParameter.Int64(1)}));
        Assert.False(wire.HasOutput());
        await wire.WriteAsync(Join(Packet('1'), Ready()));
        await Wait(statement.Prepared);
    }

    [Fact]
    public async Task WrongOwnerInvalidSignatureAndInvalidOrderWriteNoBytes()
    {
        await using var wire = new ScriptedSession();
        await using var foreign = new ScriptedSession();
        var statement = wire.Session.CreatePreparedStatement("select $1", new uint[] {20});
        await using var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await using var other = foreign.Session.CreateBatch(TestContext.Current.CancellationToken);
        Assert.Throws<ArgumentException>(() => other.SendPrepareAsync(statement));
        Assert.Throws<ArgumentException>(() => other.SendQueryAsync(statement));
        Assert.Throws<ArgumentException>(() => other.SendCloseAsync(statement));
        Assert.Throws<InvalidOperationException>(() => batch.SendQueryAsync(statement));
        Assert.Throws<InvalidOperationException>(() => batch.SendCloseAsync(statement));
        Assert.Throws<ArgumentNullException>(() => batch.SendPrepareAsync(null!));
        Assert.Throws<ArgumentNullException>(() => batch.SendQueryAsync((MpgsqlPreparedStatement)null!));
        Assert.Throws<ArgumentNullException>(() => batch.SendCloseAsync(null!));
        Assert.False(wire.HasOutput());
        Assert.False(foreign.HasOutput());
        await batch.SendPrepareAsync(statement);
        Assert.Equal("P", new string(Tags(await wire.ReadOutputAsync())));
        Assert.Throws<InvalidOperationException>(() => batch.SendPrepareAsync(statement));
        Assert.Throws<ArgumentException>(() => batch.SendQueryAsync(statement));
        Assert.Throws<ArgumentException>(() => batch.SendQueryAsync(statement, new[] {MpgsqlParameter.Int64Array(null)}));
        Assert.Throws<InvalidOperationException>(() => batch.SendQueryAsync(statement, new MpgsqlParameter[1]));
        Assert.False(wire.HasOutput());
        await batch.SendSyncAsync();
        await wire.WriteAsync(Join(Packet('1'), Ready()));
        await Wait(statement.Prepared);
        await using var emptyReader = await batch.ReadResultsAsync();
        Assert.Equal(-1, emptyReader.QueryIndex);
        Assert.False(await emptyReader.ReadAsync());
        Assert.False(await emptyReader.NextResultAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrSkippedPreparationFaultsOnlyAtRecoveryBoundary(bool skipped)
    {
        await using var wire = new ScriptedSession();
        var statement = wire.Session.CreatePreparedStatement("bad sql");
        var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        if (skipped) await batch.SendQueryAsync("earlier bad sql");
        await batch.SendPrepareAsync(statement);
        await batch.SendQueryAsync(statement);
        await batch.SendSyncAsync();
        await wire.WriteAsync(Error("42601"));
        Assert.False(statement.Prepared.IsCompleted);
        Assert.False(batch.Completion.IsCompleted);
        await wire.WriteAsync(Ready());
        var error = await FailBatch(batch);
        Assert.Equal(skipped ? 0 : (int?)null, error.QueryIndex);
        Assert.Same(error, await Assert.ThrowsAsync<MpgsqlServerException>(() => Wait(statement.Prepared)));
        Assert.Equal(0, wire.Session.TrackedStatementCount);
        await using var next = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        Assert.Throws<InvalidOperationException>(() => next.SendPrepareAsync(statement));
        Assert.Throws<InvalidOperationException>(() => next.SendQueryAsync(statement));
        await wire.ReadOutputAsync();
        await next.SendCloseAsync(statement);
        Assert.False(wire.HasOutput());
        statement.Dispose();
        await next.SendQueryAsync("select 7::bigint");
        await next.SendSyncAsync();
        await wire.WriteAsync(Join(Query(7), Ready()));
        await using var reader = await next.ReadResultsAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(7, reader.GetInt64(0));
        Assert.False(await reader.NextResultAsync());
    }

    [Fact]
    public async Task ExecutionErrorPreservesConfirmedPreparation()
    {
        await using var wire = new ScriptedSession();
        var statement = wire.Session.CreatePreparedStatement("select $1", new uint[] {20});
        var failed = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await failed.SendPrepareAsync(statement);
        await failed.SendQueryAsync(statement, new[] {MpgsqlParameter.Int64(0)});
        await failed.SendSyncAsync();
        await wire.ReadOutputAsync();
        await wire.WriteAsync(Join(Packet('1'), Packet('2'), Description(20), Error(), Ready()));
        Assert.Equal(0, (await FailBatch(failed)).QueryIndex);
        await Wait(statement.Prepared);
        await using var next = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await next.SendQueryAsync(statement, new[] {MpgsqlParameter.Int64(42)});
        await next.SendSyncAsync();
        Assert.Equal("BDES", new string(Tags(await wire.ReadOutputAsync())));
        await wire.WriteAsync(Join(Execution(42), Ready()));
        await using var reader = await next.ReadResultsAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(42, reader.GetInt64(0));
        Assert.False(await reader.NextResultAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrSkippedCloseRetiresStatementAndCanBeRetried(bool skipped)
    {
        await using var wire = new ScriptedSession();
        var statement = await Prepare(wire);
        var failed = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        if (skipped) await failed.SendQueryAsync(statement, new[] {MpgsqlParameter.Int64(0)});
        await failed.SendCloseAsync(statement);
        Assert.Throws<InvalidOperationException>(() => failed.SendQueryAsync(statement, new[] {MpgsqlParameter.Int64(1)}));
        Assert.Throws<InvalidOperationException>(() => failed.SendCloseAsync(statement));
        await failed.SendSyncAsync();
        await wire.WriteAsync(Join(Error(), Ready()));
        Assert.Equal(skipped ? 0 : (int?)null, (await FailBatch(failed)).QueryIndex);
        Assert.True(statement.Prepared.IsCompletedSuccessfully);
        await using var retry = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        Assert.Throws<InvalidOperationException>(() => retry.SendQueryAsync(statement, new[] {MpgsqlParameter.Int64(1)}));
        await retry.SendCloseAsync(statement);
        await retry.SendSyncAsync();
        await wire.WriteAsync(Join(Packet('3'), Ready()));
        await Wait(retry.Completion);
        await wire.ReadOutputAsync();
        await using var noop = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await noop.SendCloseAsync(statement);
        Assert.False(wire.HasOutput());
        Assert.Throws<InvalidOperationException>(() => noop.SendQueryAsync(statement, new[] {MpgsqlParameter.Int64(1)}));
    }

    [Fact]
    public async Task CancellationDropsQueuedPrepareExecuteCloseAndNeedsNoServerClose()
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var wire = new ScriptedSession(blockWrites: true);
        var statement = wire.Session.CreatePreparedStatement("select $1", new uint[] {20});
        var batch = wire.Session.CreateBatch(request.Token);
        var published = batch.SendQueryAsync("select 11::bigint").AsTask();
        var held = await wire.Outgoing.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Task[] queued =
        [
            batch.SendPrepareAsync(statement).AsTask(),
            batch.SendQueryAsync(statement, new[] {MpgsqlParameter.Int64(22)}).AsTask(),
            batch.SendCloseAsync(statement).AsTask()
        ];
        var sync = batch.SendSyncAsync().AsTask();
        request.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Wait(published));
        foreach (var send in queued) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Wait(send));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Wait(statement.Prepared));
        wire.Outgoing.Reader.AdvanceTo(held.Buffer.End);
        Assert.Equal("S", new string(Tags(await wire.ReadOutputAsync())));
        await Wait(sync);
        await wire.WriteAsync(Join(Query(11), Ready()), fragment: 1);
        await Wait(batch.Completion);
        await batch.DisposeAsync();
        await using var retry = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await retry.SendCloseAsync(statement);
        Assert.Equal(0, wire.Session.TrackedStatementCount);
        Assert.False(wire.HasOutput());
        statement.Dispose();
    }

    [Fact]
    public async Task PublishedPreparationSurvivesLogicalCancellation()
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var wire = new ScriptedSession(blockWrites: true);
        var statement = wire.Session.CreatePreparedStatement("select 1::bigint");
        var batch = wire.Session.CreateBatch(request.Token);
        var send = batch.SendPrepareAsync(statement).AsTask();
        var held = await wire.Outgoing.Reader.ReadAsync(TestContext.Current.CancellationToken);
        var sync = batch.SendSyncAsync().AsTask();
        request.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Wait(send));
        Assert.False(statement.Prepared.IsCompleted);
        Assert.Equal(1, wire.Session.TrackedStatementCount);
        Assert.Throws<InvalidOperationException>(statement.Dispose);
        wire.Outgoing.Reader.AdvanceTo(held.Buffer.End);
        Assert.Equal("S", new string(Tags(await wire.ReadOutputAsync())));
        await Wait(sync);
        await wire.WriteAsync(Join(Packet('1'), Ready()));
        await Wait(statement.Prepared);
        await Wait(batch.Completion);
        await batch.DisposeAsync();
        Assert.Equal(1, wire.Session.TrackedStatementCount);
        Assert.Throws<InvalidOperationException>(statement.Dispose);
    }

    [Fact]
    public async Task SessionDisposalFaultsPendingPreparationAndInvalidatesConfirmedStatements()
    {
        await using var wire = new ScriptedSession();
        var confirmed = await Prepare(wire);
        var pending = wire.Session.CreatePreparedStatement("select 1::bigint");
        var unused = wire.Session.CreatePreparedStatement("select 2::bigint");
        var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await batch.SendPrepareAsync(pending);
        await wire.Session.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Wait(pending.Prepared));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Wait(unused.Prepared));
        Assert.True(confirmed.Prepared.IsCompletedSuccessfully);
        Assert.Equal(0, wire.Session.TrackedStatementCount);
        confirmed.Dispose();
        pending.Dispose();
        unused.Dispose();
        Assert.Throws<ObjectDisposedException>(() => batch.SendQueryAsync(confirmed, new[] {MpgsqlParameter.Int64(1)}));
    }

    [Fact]
    public async Task AsynchronousMessagesDoNotConsumePreparationOrExecutionResponses()
    {
        await using var wire = new ScriptedSession();
        var statement = wire.Session.CreatePreparedStatement("select $1", new uint[] {20});
        await using var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await batch.SendPrepareAsync(statement);
        await batch.SendQueryAsync(statement, new[] {MpgsqlParameter.Int64(42)});
        await batch.SendCloseAsync(statement);
        await batch.SendSyncAsync();
        var notice = Packet('N', System.Text.Encoding.UTF8.GetBytes("SNOTICE\0Mhello\0\0"));
        var parameter = Packet('S', System.Text.Encoding.UTF8.GetBytes("application_name\0prepared-tests\0"));
        var notification = Packet('A', [0, 0, 0, 7, .. System.Text.Encoding.UTF8.GetBytes("events\0ready\0")]);
        await wire.WriteAsync(Join(notice, Packet('1'), parameter, Packet('2'), notification,
            Description(20), Row(Int64(42)), Command(), notice, Packet('3'), Ready()), fragment: 1);
        await Wait(statement.Prepared);
        await using var reader = await batch.ReadResultsAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(42, reader.GetInt64(0));
        Assert.False(await reader.NextResultAsync());
        Assert.True(wire.Session.TryReadNotice(out _));
        Assert.True(wire.Session.TryReadNotice(out _));
        Assert.True(wire.Session.TryReadNotification(out var received));
        Assert.Equal("events", received.Channel);
        Assert.True(wire.Session.TryGetParameter("application_name", out var name));
        Assert.Equal("prepared-tests", name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedNoDataAndEmptyQueryStillHaveOneResult(bool empty)
    {
        await using var wire = new ScriptedSession();
        var statement = wire.Session.CreatePreparedStatement(empty ? " " : "update t set x=1");
        await using var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await batch.SendPrepareAsync(statement);
        await batch.SendQueryAsync(statement);
        await batch.SendSyncAsync();
        await wire.WriteAsync(Join(Packet('1'), Packet('2'), Packet('n'),
            empty ? Packet('I') : Command("UPDATE 1"), Ready()));
        await using var reader = await batch.ReadResultsAsync();
        Assert.Equal(0, reader.QueryIndex);
        Assert.True(reader.Columns.IsEmpty);
        Assert.False(await reader.ReadAsync());
        Assert.Equal(empty ? null : "UPDATE 1", reader.CommandTag);
        Assert.False(await reader.NextResultAsync());
    }

    [Fact]
    public void PreparedEncoderChecksCompleteCapacityAndValidationBeforeWriting()
    {
        MpgsqlParameter[] parameters = [MpgsqlParameter.Int64(42)];
        byte[] destination = [.. Enumerable.Repeat((byte)0xa5, QueryPacket.GetPreparedByteCount("statement", parameters) - 1)];
        byte[] before = [.. destination];
        Assert.Throws<ArgumentException>(() => QueryPacket.WritePrepared("statement", parameters, destination));
        Assert.Equal(before, destination);
        Assert.Throws<InvalidOperationException>(() => QueryPacket.WritePrepared("statement", new MpgsqlParameter[1], destination));
        Assert.Equal(before, destination);
    }

    [Fact]
    public void PreparedNullEmptyAndNullableArrayPayloadsMatchLowLevelComposition()
    {
        MpgsqlParameter[] parameters =
        [
            MpgsqlParameter.Int64(null), MpgsqlParameter.Int64Array(null),
            MpgsqlParameter.Int64Array(ReadOnlyMemory<long>.Empty),
            MpgsqlParameter.NullableInt64Array(new long?[] {long.MinValue, null, long.MaxValue})
        ];
        ReadOnlyMemory<byte>?[] payloads = new ReadOnlyMemory<byte>?[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
            if (parameters[i].PayloadLength >= 0)
            {
                byte[] bytes = new byte[parameters[i].PayloadLength];
                parameters[i].WritePayload(bytes);
                payloads[i] = bytes;
            }
        const string name = "statement_ж";
        var reference = new ArrayBufferWriter<byte>();
        FrontendMessage.Bind(statement: name, parameters: payloads,
            parameterFormats: new[] {FormatCode.Binary}, resultFormats: new[] {FormatCode.Binary}).Write(reference);
        FrontendMessage.Describe(StatementOrPortal.Portal).Write(reference);
        FrontendMessage.Execute().Write(reference);
        byte[] destination = new byte[QueryPacket.GetPreparedByteCount(name, parameters)];
        Assert.Equal(destination.Length, QueryPacket.WritePrepared(name, parameters, destination));
        Assert.Equal(reference.WrittenSpan.ToArray(), destination);
    }

    private static async Task<MpgsqlPreparedStatement> Prepare(ScriptedSession wire)
    {
        var statement = wire.Session.CreatePreparedStatement("select $1", new uint[] {20});
        await using var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await batch.SendPrepareAsync(statement);
        await batch.SendSyncAsync();
        await wire.ReadOutputAsync();
        await wire.WriteAsync(Join(Packet('1'), Ready()));
        await Wait(statement.Prepared);
        await Wait(batch.Completion);
        return statement;
    }

    private static async Task<MpgsqlServerException> FailBatch(MpgsqlQueryBatch batch)
    {
        var error = await Assert.ThrowsAsync<MpgsqlServerException>(() => Wait(batch.Completion));
        await Assert.ThrowsAsync<MpgsqlServerException>(() => batch.DisposeAsync().AsTask());
        return error;
    }
}
