using System.Buffers;
using System.Buffers.Binary;
using System.Data.Common;
using Mpgsql.Tests.Protocol;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class AdoCursorExecutionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory, InlineData(false), InlineData(true)]
    public async Task GenericScalarUsesSourceCustomMappingOnlyForItsSelectedValue(bool batchPath)
    {
        await using var wire = new ScriptedSession();
        var conversions = 0;
        var mapper = new MpgsqlTypeMapper().Register<Identifier>(90001, payload =>
        {
            Interlocked.Increment(ref conversions);
            return new Identifier(BinaryPrimitives.ReadInt64BigEndian(payload.ToArray()));
        });
        await using var source = new MpgsqlDataSource(_ => ValueTask.FromResult(wire.Session),
            (_, _) => ValueTask.CompletedTask, new MpgsqlDataSourceOptions { TypeMapper = mapper });
        mapper.Register<Identifier>(90001, _ => throw new InvalidOperationException("The source must retain its mapping snapshot."));
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand("select custom scalar");
        await using var batch = connection.CreateBatch();
        batch.BatchCommands.Add(new MpgsqlBatchCommand("select custom scalar"));

        for (var iteration = 0; iteration < 3; iteration++)
        {
            var operation = (batchPath ? batch.ExecuteScalarAsync<Identifier>(Token)
                : command.ExecuteScalarAsync<Identifier>(Token)).AsTask();
            await Sync(wire);
            var reply = iteration switch
            {
                0 => Join(Begin(90001), Row(Int64(42)), Row(Int64(43)), Command("SELECT 2"), Ready()),
                1 => Join(Begin(90001), Row((byte[]?)null), Command(), Ready()),
                _ => Join(Begin(90001), Command("SELECT 0"), Ready())
            };
            var writing = wire.WriteAsync(reply, 1);
            var result = await operation.WaitAsync(TestTimeout, Token);
            await writing.WaitAsync(TestTimeout, Token);
            Assert.Equal(iteration != 2, result.HasRow);
            Assert.Equal(iteration == 1, result.IsNull);
            if (iteration == 0)
                Assert.Equal(new Identifier(42), result.Value);
            else
                Assert.Throws<InvalidOperationException>(() => result.Value);
            Assert.Equal(1, Volatile.Read(ref conversions));
            Assert.True(wire.Session.IsIdleAndHealthy);
        }
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task StandardScalarKeepsTheFirstEmptyOrNullRowSetAndDrainsLaterValues(bool sqlNull)
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire);
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var batch = connection.CreateBatch();
        foreach (var sql in new[] { "update before scalar", "select first scalar", "select later scalar" })
            batch.BatchCommands.Add(new MpgsqlBatchCommand(sql));
        DbBatch standard = batch;
        var operation = standard.ExecuteScalarAsync(Token);
        await Sync(wire);
        Assert.Throws<InvalidOperationException>(() => batch.BatchCommands[2].CommandText = "changed while draining");
        var writing = wire.WriteAsync(Join(Packet('1'), Packet('2'), Packet('n'), Command("UPDATE 2"), Begin(20),
            sqlNull ? Row((byte[]?)null) : [], Command(sqlNull ? "SELECT 1" : "SELECT 0"), Query(99), Ready()), 1);
        var result = await operation.WaitAsync(TestTimeout, Token);
        await writing.WaitAsync(TestTimeout, Token);
        if (sqlNull)
            Assert.Same(DBNull.Value, result);
        else
            Assert.Null(result);
        Assert.Equal(2, batch.BatchCommands[0].RecordsAffected64);
        Assert.True(wire.Session.IsIdleAndHealthy);
        batch.BatchCommands[2].CommandText = "mutable after complete drain";
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task SelectedScalarStillObservesLaterBatchErrorAndLeavesSkippedCommandsUnconfirmed(bool generic)
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire);
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var batch = connection.CreateBatch();
        foreach (var sql in new[] { "select first value", "select parse failure", "update skipped" })
            batch.BatchCommands.Add(new MpgsqlBatchCommand(sql));
        Task operation = generic ? batch.ExecuteScalarAsync<long>(Token).AsTask() : batch.ExecuteScalarAsync(Token);
        await Sync(wire);
        var firstWriting = wire.WriteAsync(Query(7), 1);
        await firstWriting.WaitAsync(TestTimeout, Token);
        Assert.False(operation.IsCompleted);
        var errorWriting = wire.WriteAsync(Error("42601"), 1);
        await errorWriting.WaitAsync(TestTimeout, Token);
        Assert.False(operation.IsCompleted);
        var readyWriting = wire.WriteAsync(Ready());
        var error = await Assert.ThrowsAsync<MpgsqlPostgresException>(() => operation.WaitAsync(TestTimeout, Token));
        await readyWriting.WaitAsync(TestTimeout, Token);
        Assert.Equal("42601", error.SqlState);
        Assert.Equal(1, error.QueryIndex);
        Assert.Equal(-1, batch.BatchCommands[1].RecordsAffected64);
        Assert.Equal(-1, batch.BatchCommands[2].RecordsAffected64);
        Assert.True(wire.Session.IsIdleAndHealthy);
        batch.BatchCommands[2].CommandText = "mutable after skipped command recovery";

        await using var next = connection.CreateCommand("select after scalar error");
        var nextOperation = next.ExecuteScalarAsync<long>(Token).AsTask();
        await Sync(wire);
        var nextWriting = wire.WriteAsync(Join(Query(9), Ready()), 1);
        Assert.Equal(9L, (await nextOperation.WaitAsync(TestTimeout, Token)).Value);
        await nextWriting.WaitAsync(TestTimeout, Token);
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task NonQueryDiscardsUnknownBinaryValuesButStillChecksTheirFraming(bool malformed)
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire);
        await using var connection = await source.OpenConnectionAsync(Token);
        await using DbCommand command = connection.CreateCommand("update returning unknown values");
        var operation = command.ExecuteNonQueryAsync(Token);
        await Sync(wire);
        var rows = malformed
            ? Packet('D', TestWire.Bytes("0002 7fffffff ff ffffffff"))
            : Join(Row(new byte[] { 0xff }, null), Row(null, "ignored"u8.ToArray()));
        var writing = wire.WriteAsync(Join(Begin(90001, 25), rows, Command("UPDATE 4"), malformed ? Ready() : []), 1);
        if (malformed)
        {
            var error = await Assert.ThrowsAsync<MpgsqlException>(() => operation.WaitAsync(TestTimeout, Token));
            Assert.IsType<InvalidDataException>(error.InnerException);
            _ = await Record.ExceptionAsync(() => writing.WaitAsync(TestTimeout, Token));
            Assert.False(wire.Session.IsHealthy);
        }
        else
        {
            await writing.WaitAsync(TestTimeout, Token);
            Assert.False(operation.IsCompleted);
            var readyWriting = wire.WriteAsync(Ready());
            Assert.Equal(4, await operation.WaitAsync(TestTimeout, Token));
            await readyWriting.WaitAsync(TestTimeout, Token);
            Assert.True(wire.Session.IsIdleAndHealthy);
        }
        Assert.Equal(0, wire.Session.BufferedRowBytes);
        Assert.Equal(0, wire.Session.CopiedRowBytes);
    }

    private static MpgsqlDataSource Source(ScriptedSession wire)
        => new(_ => ValueTask.FromResult(wire.Session), (_, _) => ValueTask.CompletedTask);

    private static async Task Sync(ScriptedSession wire)
    {
        var bytes = new List<byte>();
        do { bytes.AddRange(await wire.ReadOutputAsync()); }
        while (!Tags([.. bytes]).Contains('S'));
    }

    private sealed record Identifier(long Value);
}
