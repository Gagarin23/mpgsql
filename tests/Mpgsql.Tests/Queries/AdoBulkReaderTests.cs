using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using Mpgsql.Converters;
using Mpgsql.Tests.Protocol;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class AdoBulkReaderTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory, InlineData(1), InlineData(17), InlineData(int.MaxValue)]
    public async Task PortionsIncludePrefetchDoNotRepeatPositionedRowsAndStopAtResultBoundaries(int fragment)
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire);
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var batch = connection.CreateBatch();
        batch.BatchCommands.Add(new MpgsqlBatchCommand("select first portion"));
        batch.BatchCommands.Add(new MpgsqlBatchCommand("select second result"));
        var opening = batch.ExecuteReaderAsync(Token);
        await Sync(wire);
        var writing = wire.WriteAsync(Join(Begin(20), Row(Int64(1)), Row(Int64(2)), Row(Int64(3)),
            Row(Int64(4)), Row(Int64(5)), Command("SELECT 5"),
            Begin(20, 25), Row(Int64(9), "next"u8.ToArray()), Command(), Ready()), fragment);
        await using var reader = await opening.WaitAsync(TestTimeout, Token);
        Assert.True(reader.HasRows);
        Assert.Equal(0, await reader.ReadColumnAsync<long>(0, Memory<long>.Empty, Token));
        Assert.Equal(0, await reader.ReadRowsAsync<long, BigintMapper>(Memory<long>.Empty, default, Token));
        Assert.Throws<InvalidOperationException>(() => reader.GetInt64(0));

        long[] first = [-1, -1];
        Assert.Equal(2, await reader.ReadColumnAsync(0, first.AsMemory(), Token));
        Assert.Equal(new long[] { 1, 2 }, first);
        Assert.Equal(2L, reader.GetInt64(0)); // A full portion performs no lookahead.
        long[] projected = [-1];
        Assert.Equal(1, await reader.ReadRowsAsync<long, BigintMapper>(projected.AsMemory(), default, Token));
        Assert.Equal(3L, projected[0]);
        Assert.Equal(3L, reader.GetInt64(0));
        Assert.True(await reader.ReadAsync(Token));
        Assert.Equal(4L, reader.GetInt64(0));
        Assert.Equal(0, await reader.ReadColumnAsync<long>(0, Memory<long>.Empty, Token));
        Assert.Equal(4L, reader.GetInt64(0));

        long[] tail = [-1, -1];
        Assert.Equal(1, await reader.ReadColumnAsync(0, tail.AsMemory(), Token));
        Assert.Equal(new long[] { 5, -1 }, tail);
        Assert.Equal(0, reader.QueryIndex);
        Assert.Equal(1, reader.FieldCount);
        Assert.Equal("SELECT 5", reader.CommandTag);
        Assert.Throws<InvalidOperationException>(() => reader.GetInt64(0));
        Assert.Equal(0, await reader.ReadColumnAsync(0, tail.AsMemory(), Token));
        Assert.True(await reader.NextResultAsync(Token));
        Assert.Equal(1, reader.QueryIndex);
        Assert.Equal(2, reader.FieldCount);
        Assert.Equal(1, await reader.ReadRowsAsync<long, BigintMapper>(projected.AsMemory(), default, Token));
        Assert.Equal(9L, projected[0]);
        Assert.Equal("next", reader.GetString(1));
        Assert.False(await reader.ReadAsync(Token));
        Assert.False(await reader.NextResultAsync(Token));
        await writing.WaitAsync(TestTimeout, Token);
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task EmptyAndNonRowResultsDoNotConsumeTheFollowingResult(bool noData)
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire);
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var batch = connection.CreateBatch();
        batch.BatchCommands.Add(new MpgsqlBatchCommand("empty first result"));
        batch.BatchCommands.Add(new MpgsqlBatchCommand("select later value"));
        var opening = batch.ExecuteReaderAsync(Token);
        await Sync(wire);
        var first = noData ? Join(Packet('1'), Packet('2'), Packet('n'), Command("UPDATE 0"))
            : Join(Begin(20), Command("SELECT 0"));
        var writing = wire.WriteAsync(Join(first, Query(7), Ready()), 1);
        await using var reader = await opening.WaitAsync(TestTimeout, Token);
        long[] destination = [-1, -1];
        Assert.False(reader.HasRows);
        Assert.Equal(0, await reader.ReadRowsAsync<long, BigintMapper>(destination.AsMemory(), default, Token));
        Assert.Equal(new long[] { -1, -1 }, destination);
        Assert.Equal(0, reader.QueryIndex);
        Assert.Equal(noData ? 0 : 1, reader.FieldCount);
        Assert.True(await reader.NextResultAsync(Token));
        Assert.Equal(1, await reader.ReadColumnAsync(0, destination.AsMemory(), Token));
        Assert.Equal(new long[] { 7, -1 }, destination);
        Assert.False(await reader.NextResultAsync(Token));
        await writing.WaitAsync(TestTimeout, Token);
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Fact]
    public async Task NullableAndReferenceColumnsPreserveSqlNullWithoutCallingCustomConverters()
    {
        var conversions = 0;
        var mapper = new MpgsqlTypeMapper().Register<Identifier>(90001, payload =>
        {
            conversions++;
            return new Identifier(BinaryPrimitives.ReadInt64BigEndian(payload.ToArray()));
        });
        await using var wire = new ScriptedSession();
        await using var source = Source(wire, mapper);
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var batch = connection.CreateBatch();
        foreach (var sql in new[] { "select nullable integers", "select nullable text", "select nullable custom" })
            batch.BatchCommands.Add(new MpgsqlBatchCommand(sql));
        var opening = batch.ExecuteReaderAsync(Token);
        await Sync(wire);
        var writing = wire.WriteAsync(Join(Begin(20), Row(Int64(1)), Row((byte[]?)null), Row(Int64(3)), Command("SELECT 3"),
            Begin(25), Row(""u8.ToArray()), Row((byte[]?)null), Row("Я😀"u8.ToArray()), Command("SELECT 3"),
            Begin(90001), Row(Int64(7)), Row((byte[]?)null), Command("SELECT 2"), Ready()), 1);
        await using var reader = await opening.WaitAsync(TestTimeout, Token);
        long?[] numbers = [-1, -1, -1, -1];
        Assert.Equal(3, await reader.ReadColumnAsync(0, numbers.AsMemory(), Token));
        Assert.Equal(new long?[] { 1, null, 3, -1 }, numbers);
        Assert.True(await reader.NextResultAsync(Token));
        string?[] strings = ["sentinel", "sentinel", "sentinel", "sentinel"];
        Assert.Equal(3, await reader.ReadColumnAsync(0, strings.AsMemory(), Token));
        Assert.Equal(new string?[] { "", null, "Я😀", "sentinel" }, strings);
        Assert.True(await reader.NextResultAsync(Token));
        var sentinel = new Identifier(-1);
        Identifier?[] identifiers = [sentinel, sentinel, sentinel];
        Assert.Equal(2, await reader.ReadColumnAsync(0, identifiers.AsMemory(), Token));
        Assert.Equal(new Identifier(7), identifiers[0]);
        Assert.Null(identifiers[1]);
        Assert.Same(sentinel, identifiers[2]);
        Assert.Equal(1, conversions);
        Assert.False(await reader.NextResultAsync(Token));
        await writing.WaitAsync(TestTimeout, Token);
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Fact]
    public async Task RowFacadePreservesNullableCustomAndDbNullRepresentations()
    {
        var conversions = 0;
        var mapper = new MpgsqlTypeMapper().Register<Identifier>(90001, payload =>
        {
            conversions++;
            return new Identifier(BinaryPrimitives.ReadInt64BigEndian(payload.ToArray()));
        });
        await using var wire = new ScriptedSession();
        await using var source = Source(wire, mapper);
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand("select nullable row facade");
        var opening = command.ExecuteReaderAsync(Token);
        await Sync(wire);
        var writing = wire.WriteAsync(Join(Begin(20, 25, 90001), Row(null, null, null),
            Row(Int64(7), [], Int64(9)), Command("SELECT 2"), Ready()), 1);
        await using var reader = await opening.WaitAsync(TestTimeout, Token);
        NullableRecord[] destination = new NullableRecord[3];
        Assert.Equal(2, await reader.ReadRowsAsync<NullableRecord, NullableMapper>(destination.AsMemory(), default, Token));
        Assert.Null(destination[0].Number);
        Assert.Null(destination[0].Text);
        Assert.Null(destination[0].Identifier);
        Assert.Same(DBNull.Value, destination[0].ObjectNumber);
        Assert.True(destination[0].IsNull);
        Assert.Null(destination[0].RawNumber);
        Assert.Equal(7L, destination[1].Number);
        Assert.Equal("", destination[1].Text);
        Assert.Equal(new Identifier(9), destination[1].Identifier);
        Assert.Equal(7L, Assert.IsType<long>(destination[1].ObjectNumber));
        Assert.False(destination[1].IsNull);
        Assert.Equal(Int64(7), destination[1].RawNumber);
        Assert.Equal(1, conversions);
        Assert.False(await reader.NextResultAsync(Token));
        await writing.WaitAsync(TestTimeout, Token);
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Fact]
    public async Task NonNullableSqlNullPreservesWrittenPrefixFailedSlotAndCurrentRow()
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire);
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand("select null in portion");
        var opening = command.ExecuteReaderAsync(Token);
        await Sync(wire);
        var writing = wire.WriteAsync(Join(Begin(20), Row(Int64(1)), Row((byte[]?)null), Row(Int64(3)), Command("SELECT 3"), Ready()));
        await using var reader = await opening.WaitAsync(TestTimeout, Token);
        long[] destination = [-1, -1, -1];
        await Assert.ThrowsAsync<InvalidCastException>(() => reader.ReadColumnAsync(0, destination.AsMemory(), Token).AsTask());
        Assert.Equal(new long[] { 1, -1, -1 }, destination);
        Assert.True(reader.IsDBNull(0));
        Assert.Null(reader.GetFieldValue<long?>(0));
        Assert.False(reader.IsClosed);
        Assert.Throws<InvalidOperationException>(() => command.CommandText = "still owned after decoder failure");
        long?[] rest = [-1, -1];
        Assert.Equal(1, await reader.ReadColumnAsync(0, rest.AsMemory(), Token));
        Assert.Equal(new long?[] { 3, -1 }, rest); // The failed positioned row is not repeated.
        Assert.False(await reader.NextResultAsync(Token));
        await writing.WaitAsync(TestTimeout, Token);
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Fact]
    public async Task ColumnOrdinalAndUnsupportedRepresentationErrorsDoNotMoveThePrefetchedRow()
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire);
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand("select validate portion before movement");
        var opening = command.ExecuteReaderAsync(Token);
        await Sync(wire);
        var writing = wire.WriteAsync(Join(Query(7), Ready()));
        await using var reader = await opening.WaitAsync(TestTimeout, Token);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.ReadColumnAsync(-1, new long[1].AsMemory(), Token).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.ReadColumnAsync(1, new long[1].AsMemory(), Token).AsTask());
        await Assert.ThrowsAsync<InvalidCastException>(() => reader.ReadColumnAsync(0, new string[1].AsMemory(), Token).AsTask());
        Assert.Throws<InvalidOperationException>(() => reader.GetInt64(0));
        long[] destination = [-1];
        Assert.Equal(1, await reader.ReadColumnAsync(0, destination.AsMemory(), Token));
        Assert.Equal(7L, destination[0]);
        Assert.Equal(7L, reader.GetInt64(0));
        Assert.False(await reader.NextResultAsync(Token));
        await writing.WaitAsync(TestTimeout, Token);
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task CustomDecoderAndMapperFailuresPreserveOriginalErrorPrefixAndFailedRow(bool recordMapper)
    {
        var failure = new FormatException("projection failed on row 2");
        var typeMapper = new MpgsqlTypeMapper().Register<long>(90001, payload =>
        {
            var value = BinaryPrimitives.ReadInt64BigEndian(payload.ToArray());
            return value == 2 ? throw failure : value;
        });
        await using var wire = new ScriptedSession();
        await using var source = Source(wire, typeMapper);
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand("select failing projection");
        var opening = command.ExecuteReaderAsync(Token);
        await Sync(wire);
        var writing = wire.WriteAsync(Join(Begin(recordMapper ? 20u : 90001u), Row(Int64(1)), Row(Int64(2)),
            Row(Int64(3)), Command("SELECT 3"), Ready()), 1);
        await using var reader = await opening.WaitAsync(TestTimeout, Token);
        long[] destination = [-1, -1, -1];
        var reading = recordMapper
            ? reader.ReadRowsAsync<long, FailingMapper>(destination.AsMemory(), new FailingMapper(failure), Token)
            : reader.ReadColumnAsync(0, destination.AsMemory(), Token);
        Assert.Same(failure, await Assert.ThrowsAsync<FormatException>(() => reading.AsTask()));
        Assert.Equal(new long[] { 1, -1, -1 }, destination);
        Assert.Equal(Int64(2), reader.GetRawValue(0)!.Value.ToArray());
        Assert.False(reader.IsClosed);
        Assert.True(await reader.ReadAsync(Token));
        Assert.Equal(3L, reader.GetFieldValue<long>(0));
        Assert.False(await reader.NextResultAsync(Token));
        await writing.WaitAsync(TestTimeout, Token);
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Fact]
    public async Task MalformedValueDecoderFailureLeavesTheHealthyReaderOnItsFailedRow()
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire);
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand("select malformed bigint value");
        var opening = command.ExecuteReaderAsync(Token);
        await Sync(wire);
        byte[] malformed = [1, 2, 3, 4, 5, 6, 7];
        var writing = wire.WriteAsync(Join(Begin(20), Row(Int64(1)), Row(malformed), Row(Int64(3)), Command("SELECT 3"), Ready()), 1);
        await using var reader = await opening.WaitAsync(TestTimeout, Token);
        long[] destination = [-1, -1, -1];
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadColumnAsync(0, destination.AsMemory(), Token).AsTask());
        Assert.Equal(new long[] { 1, -1, -1 }, destination);
        Assert.Equal(malformed, reader.GetRawValue(0)!.Value.ToArray());
        Assert.True(wire.Session.IsHealthy);
        Assert.True(await reader.ReadAsync(Token));
        Assert.Equal(3L, reader.GetInt64(0));
        Assert.False(await reader.NextResultAsync(Token));
        await writing.WaitAsync(TestTimeout, Token);
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Theory, InlineData(1), InlineData(int.MaxValue)]
    public async Task RowMappingKeepsBuiltinByteaJsonbAndArrayValuesOwnedAcrossMovementAndFollowingExecution(int fragment)
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire);
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand("select owned row values");
        var opening = command.ExecuteReaderAsync(Token);
        await Sync(wire);
        var writing = wire.WriteAsync(Join(Begin(20, 17, 3802, 1016, 25, 25),
            Row(Int64(1), [7, 8, 9], [1, .. "{\"n\":1}"u8.ToArray()], ArrayPayload(1, 2), "Я😀"u8.ToArray(), null),
            Row(Int64(2), [], [1, .. "{}"u8.ToArray()], ArrayPayload(), [], null),
            Command("SELECT 2"), Ready()), fragment);
        await using var reader = await opening.WaitAsync(TestTimeout, Token);
        OwnedRecord[] destination = new OwnedRecord[3];
        Assert.Equal(2, await reader.ReadRowsAsync<OwnedRecord, OwnedMapper>(destination.AsMemory(), default, Token));
        Assert.False(await reader.NextResultAsync(Token));
        await writing.WaitAsync(TestTimeout, Token);
        Assert.Equal(6, destination[0].FieldCount);
        Assert.Equal(1016u, destination[0].ArrayOid);
        Assert.Equal(new byte[] { 7, 8, 9 }, destination[0].Bytes.ToArray());
        Assert.Equal("{\"n\":1}"u8.ToArray(), destination[0].Json.ToArray());
        Assert.Equal(new long[] { 1, 2 }, destination[0].Numbers.ToArray());
        Assert.Equal("Я😀", destination[0].Text);
        Assert.True(destination[0].NullField);
        Assert.Equal(Int64(1), destination[0].Raw);
        Assert.True(destination[1].Bytes.IsEmpty);
        Assert.True(destination[1].Numbers.IsEmpty);
        Assert.Equal("", destination[1].Text);
        await reader.CloseAsync();

        command.CommandText = "select after owned values";
        var next = command.ExecuteScalarAsync<long>(Token).AsTask();
        await Sync(wire);
        var nextWriting = wire.WriteAsync(Join(Query(99), Ready()), 1);
        Assert.Equal(99L, (await next.WaitAsync(TestTimeout, Token)).Value);
        await nextWriting.WaitAsync(TestTimeout, Token);
        Assert.Equal(new byte[] { 7, 8, 9 }, destination[0].Bytes.ToArray());
        Assert.Equal("{\"n\":1}"u8.ToArray(), destination[0].Json.ToArray());
        Assert.Equal(new long[] { 1, 2 }, destination[0].Numbers.ToArray());
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Fact]
    public async Task ArrayColumnBulkRetainsEachOwnedArrayAndNullableArrayDistinction()
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire);
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand("select arrays in portion");
        var opening = command.ExecuteReaderAsync(Token);
        await Sync(wire);
        var writing = wire.WriteAsync(Join(Begin(1016), Row(ArrayPayload(1, 2)), Row(ArrayPayload()), Row((byte[]?)null),
            Row(ArrayPayload(3, 4)), Command("SELECT 4"), Ready()), 1);
        await using var reader = await opening.WaitAsync(TestTimeout, Token);
        ReadOnlyMemory<long>?[] destination = new ReadOnlyMemory<long>?[5];
        Assert.Equal(4, await reader.ReadColumnAsync(0, destination.AsMemory(), Token));
        Assert.Equal(new long[] { 1, 2 }, destination[0]!.Value.ToArray());
        Assert.True(destination[1]!.Value.IsEmpty);
        Assert.Null(destination[2]);
        Assert.Equal(new long[] { 3, 4 }, destination[3]!.Value.ToArray());
        Assert.False(await reader.NextResultAsync(Token));
        await writing.WaitAsync(TestTimeout, Token);
        await reader.CloseAsync();
        Assert.Equal(new long[] { 1, 2 }, destination[0]!.Value.ToArray());
        Assert.Equal(new long[] { 3, 4 }, destination[3]!.Value.ToArray());
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Fact]
    public async Task PendingPortionRejectsOtherMovementWithoutDiscardingItsActiveOwner()
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire);
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand("select pending portion");
        var opening = command.ExecuteReaderAsync(Token);
        await Sync(wire);
        var initialWrite = wire.WriteAsync(Join(Begin(20), Row(Int64(1))));
        await using var reader = await opening.WaitAsync(TestTimeout, Token);
        long[] destination = [-1, -1, -1];
        var reading = reader.ReadColumnAsync(0, destination.AsMemory(), Token).AsTask();
        await initialWrite.WaitAsync(TestTimeout, Token);
        Assert.False(reading.IsCompleted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.NextResultAsync(Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadColumnAsync(0, new long[1].AsMemory(), Token).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadRowsAsync<long, BigintMapper>(new long[1].AsMemory(), default, Token).AsTask());
        Assert.False(reader.IsClosed);
        Assert.True(wire.Session.IsHealthy);
        Assert.Throws<InvalidOperationException>(() => command.CommandText = "active portion still owns command");
        var remainingWrite = wire.WriteAsync(Join(Row(Int64(2)), Row(Int64(3)), Command("SELECT 3"), Ready()));
        Assert.Equal(3, await reading.WaitAsync(TestTimeout, Token));
        Assert.Equal(new long[] { 1, 2, 3 }, destination);
        Assert.Equal(3L, reader.GetInt64(0));
        Assert.False(await reader.NextResultAsync(Token));
        await remainingWrite.WaitAsync(TestTimeout, Token);
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Fact]
    public async Task ReentrantMapperMovementIsRejectedWhileItsRowFacadeRemainsReadable()
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire);
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand("select reentrant projection");
        var opening = command.ExecuteReaderAsync(Token);
        await Sync(wire);
        var writing = wire.WriteAsync(Join(Begin(20), Row(Int64(1)), Row(Int64(2)), Command("SELECT 2"), Ready()));
        await using var reader = await opening.WaitAsync(TestTimeout, Token);
        long[] destination = [-1, -1, -1];
        Assert.Equal(2, await reader.ReadRowsAsync<long, ReentrantMapper>(destination.AsMemory(), new ReentrantMapper(reader), Token));
        Assert.Equal(new long[] { 1, 2, -1 }, destination);
        Assert.False(await reader.NextResultAsync(Token));
        await writing.WaitAsync(TestTimeout, Token);
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task CancelledPortionWaitsForReadyForQueryAndCannotCancelFollowingExecution(bool alreadyCancelled)
    {
        await using var wire = new ScriptedSession();
        var cancelSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancels = 0;
        await using var source = new MpgsqlDataSource(_ => ValueTask.FromResult(wire.Session), (_, _) =>
        {
            Interlocked.Increment(ref cancels);
            cancelSent.TrySetResult();
            return ValueTask.CompletedTask;
        });
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand("select cancelled portion");
        var opening = command.ExecuteReaderAsync(Token);
        await Sync(wire);
        var initialWrite = wire.WriteAsync(Join(Begin(20), Row(Int64(1))));
        await using var reader = await opening.WaitAsync(TestTimeout, Token);
        using var cancellation = new CancellationTokenSource();
        if (alreadyCancelled)
            cancellation.Cancel();
        long[] destination = [-1, -1, -1];
        var reading = reader.ReadColumnAsync(0, destination.AsMemory(), cancellation.Token).AsTask();
        if (!alreadyCancelled)
        {
            await initialWrite.WaitAsync(TestTimeout, Token);
            Assert.False(reading.IsCompleted);
            cancellation.Cancel();
        }
        await cancelSent.Task.WaitAsync(TestTimeout, Token);
        await initialWrite.WaitAsync(TestTimeout, Token);
        Assert.False(reading.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => command.CommandText = "recovery still owns command");
        await wire.WriteAsync(Error("57014"), 1);
        Assert.False(reading.IsCompleted);
        var readyWriting = wire.WriteAsync(Ready());
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading.WaitAsync(TestTimeout, Token));
        await readyWriting.WaitAsync(TestTimeout, Token);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(new long[] { alreadyCancelled ? -1 : 1, -1, -1 }, destination);
        Assert.True(wire.Session.IsIdleAndHealthy);
        command.CommandText = "select next after bulk cancellation";
        var next = command.ExecuteScalarAsync<long>(Token).AsTask();
        await Sync(wire);
        var nextWriting = wire.WriteAsync(Join(Query(9), Ready()), 1);
        Assert.Equal(9L, (await next.WaitAsync(TestTimeout, Token)).Value);
        await nextWriting.WaitAsync(TestTimeout, Token);
        Assert.Equal(1, Volatile.Read(ref cancels));
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task CommandCancellationBeforeBulkOnPrefetchedRowRecoversBeforeReleasingItsConnection(bool recordMapper)
    {
        await using var wire = new ScriptedSession();
        var cancelSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancels = 0;
        await using var source = new MpgsqlDataSource(_ => ValueTask.FromResult(wire.Session), (_, _) =>
        {
            Interlocked.Increment(ref cancels);
            cancelSent.TrySetResult();
            return ValueTask.CompletedTask;
        });
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand("select cancelled before bulk starts");
        var opening = command.ExecuteReaderAsync(Token);
        await Sync(wire);
        var initialWrite = wire.WriteAsync(Join(Begin(20), Row(Int64(42))));
        await using var reader = await opening.WaitAsync(TestTimeout, Token);
        Assert.True(reader.HasRows);
        command.Cancel();
        await cancelSent.Task.WaitAsync(TestTimeout, Token);
        long[] destination = [-1, -1];
        var reading = (recordMapper
            ? reader.ReadRowsAsync<long, BigintMapper>(destination.AsMemory(), default, Token)
            : reader.ReadColumnAsync(0, destination.AsMemory(), Token)).AsTask();
        await initialWrite.WaitAsync(TestTimeout, Token);
        Assert.False(reading.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => command.CommandText = "not released before recovery");
        await wire.WriteAsync(Error("57014"), 1);
        Assert.False(reading.IsCompleted);
        var readyWriting = wire.WriteAsync(Ready());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading.WaitAsync(TestTimeout, Token));
        await readyWriting.WaitAsync(TestTimeout, Token);
        Assert.Equal(new long[] { -1, -1 }, destination);
        Assert.True(wire.Session.IsIdleAndHealthy);
        command.CommandText = "select after cancellation before bulk";
        var nextReading = command.ExecuteScalarAsync<long>(Token).AsTask();
        await Sync(wire);
        var nextWriting = wire.WriteAsync(Join(Query(9), Ready()), 1);
        Assert.Equal(9L, (await nextReading.WaitAsync(TestTimeout, Token)).Value);
        await nextWriting.WaitAsync(TestTimeout, Token);
        Assert.Equal(1, Volatile.Read(ref cancels));
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task PrecanceledRegistrationCallbackFailureReleasesBulkClaimBeforeRecovery(bool recordMapper)
    {
        await using var wire = new RegistrationCallbackWire();
        var cancelSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var source = new MpgsqlDataSource(_ => ValueTask.FromResult(wire.Session), (_, _) =>
        {
            cancelSent.TrySetResult();
            return ValueTask.CompletedTask;
        });
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand("select registration callback failure");
        var opening = command.ExecuteReaderAsync(Token);
        await wire.ReadSyncAsync();
        var initialWrite = wire.WriteAsync(Join(Begin(20), Row(Int64(42))));
        await using var reader = await opening.WaitAsync(TestTimeout, Token);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var failure = new InvalidOperationException("CancelPendingRead failed while registering the bulk token.");
        wire.Reader.FailNextWake(failure);
        long[] destination = [-1, -1];
        var reading = (recordMapper
            ? reader.ReadRowsAsync<long, BigintMapper>(destination.AsMemory(), default, cancellation.Token)
            : reader.ReadColumnAsync(0, destination.AsMemory(), cancellation.Token)).AsTask();
        await wire.Reader.WakeFailed.Task.WaitAsync(TestTimeout, Token);
        await cancelSent.Task.WaitAsync(TestTimeout, Token);
        await initialWrite.WaitAsync(TestTimeout, Token);
        Assert.False(reading.IsCompleted);
        var recoveryWrite = wire.WriteAsync(Join(Error("57014"), Ready()));
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading.WaitAsync(TestTimeout, Token));
        await recoveryWrite.WaitAsync(TestTimeout, Token);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Same(failure, error.InnerException);
        Assert.Equal(new long[] { -1, -1 }, destination);
        Assert.Equal(1, wire.Reader.FailedWakes);
        Assert.True(wire.Session.IsIdleAndHealthy);
        command.CommandText = "select after registration callback failure";
        var nextReading = command.ExecuteScalarAsync<long>(Token).AsTask();
        await wire.ReadSyncAsync();
        var nextWriting = wire.WriteAsync(Join(Query(9), Ready()));
        Assert.Equal(9L, (await nextReading.WaitAsync(TestTimeout, Token)).Value);
        await nextWriting.WaitAsync(TestTimeout, Token);
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task AsyncCloseOrOwnerDisposalWaitsForPendingPortionAndDrainsTheRemainingRows(bool closeReader)
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire);
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand("select disposed portion");
        var opening = command.ExecuteReaderAsync(Token);
        await Sync(wire);
        var initialWrite = wire.WriteAsync(Join(Begin(20), Row(Int64(1))));
        await using var reader = await opening.WaitAsync(TestTimeout, Token);
        long[] destination = [-1, -1, -1];
        var reading = reader.ReadColumnAsync(0, destination.AsMemory(), Token).AsTask();
        await initialWrite.WaitAsync(TestTimeout, Token);
        Assert.False(reading.IsCompleted);
        var closing = closeReader ? reader.CloseAsync() : command.DisposeAsync().AsTask();
        Assert.False(closing.IsCompleted);
        var remainingWrite = wire.WriteAsync(Join(Row(Int64(2)), Command("SELECT 2"), Ready()), 1);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => reading.WaitAsync(TestTimeout, Token));
        await closing.WaitAsync(TestTimeout, Token);
        await remainingWrite.WaitAsync(TestTimeout, Token);
        Assert.Equal(new long[] { 1, -1, -1 }, destination);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => reader.ReadColumnAsync(0, new long[1].AsMemory(), Token).AsTask());
        Assert.True(wire.Session.IsIdleAndHealthy);
        await using var next = connection.CreateCommand("select after disposed portion");
        var nextReading = next.ExecuteScalarAsync<long>(Token).AsTask();
        await Sync(wire);
        var nextWriting = wire.WriteAsync(Join(Query(9), Ready()));
        Assert.Equal(9L, (await nextReading.WaitAsync(TestTimeout, Token)).Value);
        await nextWriting.WaitAsync(TestTimeout, Token);
    }

    [Fact]
    public async Task OwnerDisposalWaitsForPausedDecoderBeforeReleasingBorrowedInputOrWritingItsSlot()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var retainedValue = 0L;
        var mapper = new MpgsqlTypeMapper().Register<long>(90001, payload =>
        {
            entered.TrySetResult();
            if (!release.Wait(TestTimeout, Token))
                throw new TimeoutException("The test did not release the paused decoder.");
            retainedValue = BinaryPrimitives.ReadInt64BigEndian(payload.ToArray());
            return retainedValue;
        });
        await using var wire = new ScriptedSession();
        await using var source = Source(wire, mapper);
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand("select paused custom decoder");
        var opening = command.ExecuteReaderAsync(Token);
        await Sync(wire);
        var initialWrite = wire.WriteAsync(Join(Begin(90001), Row(Int64(42))));
        await using var reader = await opening.WaitAsync(TestTimeout, Token);
        long[] destination = [-1];
        var reading = Task.Run(async () => await reader.ReadColumnAsync(0, destination.AsMemory(), Token), Token);
        Task? closing = null;
        try
        {
            await entered.Task.WaitAsync(TestTimeout, Token);
            closing = command.DisposeAsync().AsTask();
            Assert.False(closing.IsCompleted);
            Assert.False(initialWrite.IsCompleted); // The claim retains the borrowed row during decoding.
            Assert.Equal(-1L, destination[0]);
        }
        finally { release.Set(); }
        await initialWrite.WaitAsync(TestTimeout, Token);
        var completionWrite = wire.WriteAsync(Join(Command(), Ready()), 1);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => reading.WaitAsync(TestTimeout, Token));
        Assert.NotNull(closing);
        await closing.WaitAsync(TestTimeout, Token);
        await completionWrite.WaitAsync(TestTimeout, Token);
        Assert.Equal(42L, retainedValue);
        Assert.Equal(-1L, destination[0]);
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Fact]
    public async Task InvalidWireRowEndsPortionWithoutWritingItsSlotAndRetiresTransport()
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire);
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand("select invalid row framing");
        var opening = command.ExecuteReaderAsync(Token);
        await Sync(wire);
        var initialWrite = wire.WriteAsync(Join(Begin(20), Row(Int64(1))));
        await using var reader = await opening.WaitAsync(TestTimeout, Token);
        long[] destination = [-1, -1];
        var reading = reader.ReadColumnAsync(0, destination.AsMemory(), Token).AsTask();
        await initialWrite.WaitAsync(TestTimeout, Token);
        var malformedWrite = wire.WriteAsync(Packet('D', TestWire.Bytes("0001 7fffffff ff")), 1);
        var error = await Assert.ThrowsAsync<MpgsqlException>(() => reading.WaitAsync(TestTimeout, Token));
        Assert.IsType<InvalidDataException>(error.InnerException);
        _ = await Record.ExceptionAsync(() => malformedWrite.WaitAsync(TestTimeout, Token));
        Assert.Equal(new long[] { 1, -1 }, destination);
        Assert.False(wire.Session.IsHealthy);
        Assert.Equal(0, wire.Session.BufferedRowBytes);
    }

    [Fact]
    public async Task ServerErrorDuringPortionWaitsForRecoveryAndLeavesLaterBatchCommandsSkipped()
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire);
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var batch = connection.CreateBatch();
        batch.BatchCommands.Add(new MpgsqlBatchCommand("select rows then error"));
        batch.BatchCommands.Add(new MpgsqlBatchCommand("update skipped command"));
        var opening = batch.ExecuteReaderAsync(Token);
        await Sync(wire);
        var initialWrite = wire.WriteAsync(Join(Begin(20), Row(Int64(1))));
        await using var reader = await opening.WaitAsync(TestTimeout, Token);
        long[] destination = [-1, -1, -1];
        var reading = reader.ReadColumnAsync(0, destination.AsMemory(), Token).AsTask();
        await initialWrite.WaitAsync(TestTimeout, Token);
        await wire.WriteAsync(Error("22012"), 1);
        Assert.False(reading.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => batch.BatchCommands[1].CommandText = "still recovering");
        var readyWriting = wire.WriteAsync(Ready());
        var error = await Assert.ThrowsAsync<MpgsqlPostgresException>(() => reading.WaitAsync(TestTimeout, Token));
        await readyWriting.WaitAsync(TestTimeout, Token);
        Assert.Equal("22012", error.SqlState);
        Assert.Equal(0, error.QueryIndex);
        Assert.Equal(-1, batch.BatchCommands[0].RecordsAffected64);
        Assert.Equal(-1, batch.BatchCommands[1].RecordsAffected64);
        Assert.Equal(new long[] { 1, -1, -1 }, destination);
        Assert.True(wire.Session.IsIdleAndHealthy);
        batch.BatchCommands[1].CommandText = "mutable after skipped command recovery";
        await using var next = connection.CreateCommand("select after bulk server error");
        var nextReading = next.ExecuteScalarAsync<long>(Token).AsTask();
        await Sync(wire);
        var nextWriting = wire.WriteAsync(Join(Query(9), Ready()), 1);
        Assert.Equal(9L, (await nextReading.WaitAsync(TestTimeout, Token)).Value);
        await nextWriting.WaitAsync(TestTimeout, Token);
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    private static MpgsqlDataSource Source(ScriptedSession wire, MpgsqlTypeMapper? mapper = null)
        => new(_ => ValueTask.FromResult(wire.Session), (_, _) => ValueTask.CompletedTask,
            new MpgsqlDataSourceOptions { TypeMapper = mapper });

    private static async Task Sync(ScriptedSession wire)
    {
        var bytes = new List<byte>();
        do { bytes.AddRange(await wire.ReadOutputAsync()); }
        while (!Tags([.. bytes]).Contains('S'));
    }

    private static byte[] ArrayPayload(params long[] values)
    {
        var payload = new byte[Int64ArrayConverter.GetByteCount(values.AsMemory())];
        Int64ArrayConverter.Write(values.AsMemory(), payload);
        return payload;
    }

    private readonly struct BigintMapper : IMpgsqlRowMapper<long>
    {
        public long Read(MpgsqlRow row) => row.GetFieldValue<long>(0);
    }

    private readonly record struct FailingMapper(FormatException Failure) : IMpgsqlRowMapper<long>
    {
        public long Read(MpgsqlRow row)
        {
            var value = row.GetFieldValue<long>(0);
            return value == 2 ? throw Failure : value;
        }
    }

    private sealed record Identifier(long Value);
    private readonly record struct NullableRecord(long? Number, string? Text, Identifier? Identifier,
        object ObjectNumber, bool IsNull, byte[]? RawNumber);

    private readonly struct NullableMapper : IMpgsqlRowMapper<NullableRecord>
    {
        public NullableRecord Read(MpgsqlRow row)
            => new(row.GetFieldValue<long?>(0), row.GetFieldValue<string>(1), row.GetFieldValue<Identifier?>(2),
                row.GetFieldValue<object>(0), row.IsDBNull(0), row.GetRawValue(0)?.ToArray());
    }

    private readonly record struct OwnedRecord(ReadOnlyMemory<byte> Bytes, Memory<byte> Json, ReadOnlyMemory<long> Numbers,
        string? Text, bool NullField, byte[] Raw, int FieldCount, uint ArrayOid);

    private readonly struct OwnedMapper : IMpgsqlRowMapper<OwnedRecord>
    {
        public OwnedRecord Read(MpgsqlRow row)
        {
            return new OwnedRecord(row.GetFieldValue<ReadOnlyMemory<byte>>(1), row.GetFieldValue<Memory<byte>>(2),
                row.GetFieldValue<ReadOnlyMemory<long>>(3), row.GetFieldValue<string>(4), row.IsDBNull(5),
                row.GetRawValue(0)!.Value.ToArray(), row.FieldCount, row.Columns.Span[3].DataTypeOid);
        }
    }

    private readonly record struct ReentrantMapper(MpgsqlDataReader Reader) : IMpgsqlRowMapper<long>
    {
        public long Read(MpgsqlRow row)
        {
            var reader = Reader;
            AssertRejected(() => reader.ReadValueTaskAsync());
            AssertRejected(() => new ValueTask<bool>(reader.ReadAsync()));
            AssertRejected(() => reader.NextResultValueTaskAsync());
            AssertRejected(() => new ValueTask<bool>(reader.NextResultAsync()));
            AssertRejected(() => reader.ReadColumnAsync(0, new long[1].AsMemory()));
            AssertRejected(() => reader.ReadRowsAsync<long, BigintMapper>(new long[1].AsMemory(), default));
            Assert.False(reader.IsClosed);
            return row.GetFieldValue<long>(0);
        }

        private static void AssertRejected<T>(Func<ValueTask<T>> action)
        {
            Assert.Throws<InvalidOperationException>(() =>
            {
                var movement = action();
                Assert.True(movement.IsCompleted);
                movement.GetAwaiter().GetResult();
            });
        }
    }

    private sealed class RegistrationCallbackWire : IAsyncDisposable
    {
        private readonly Pipe _incoming = new(new PipeOptions(pauseWriterThreshold: 32, resumeWriterThreshold: 16, useSynchronizationContext: false));
        private readonly Pipe _outgoing = new(new PipeOptions(pauseWriterThreshold: 0, useSynchronizationContext: false));
        internal ThrowingWakeReader Reader { get; }
        internal MpgsqlMessageSession Session { get; }
        internal RegistrationCallbackWire()
        {
            Reader = new ThrowingWakeReader(_incoming.Reader);
            Session = new MpgsqlMessageSession(Reader, _outgoing.Writer, Token);
        }
        internal async Task WriteAsync(byte[] bytes)
        {
            await _incoming.Writer.WriteAsync(bytes, Token).AsTask().WaitAsync(TestTimeout, Token);
        }

        internal async Task ReadSyncAsync()
        {
            var bytes = new List<byte>();
            do
            {
                var read = await _outgoing.Reader.ReadAsync(Token).AsTask().WaitAsync(TestTimeout, Token);
                bytes.AddRange(read.Buffer.ToArray());
                _outgoing.Reader.AdvanceTo(read.Buffer.End);
            }
            while (!Tags([.. bytes]).Contains('S'));
        }
        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync().AsTask().WaitAsync(TestTimeout, Token);
            await _incoming.Writer.CompleteAsync();
            await _outgoing.Reader.CompleteAsync();
        }
    }

    private sealed class ThrowingWakeReader(PipeReader inner) : PipeReader
    {
        private Exception? _wakeFailure;
        private int _failedWakes;
        internal TaskCompletionSource WakeFailed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int FailedWakes => Volatile.Read(ref _failedWakes);
        internal void FailNextWake(Exception failure) => Volatile.Write(ref _wakeFailure, failure);
        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default) => inner.ReadAsync(cancellationToken);
        public override bool TryRead(out ReadResult result) => inner.TryRead(out result);
        public override void AdvanceTo(SequencePosition consumed) => inner.AdvanceTo(consumed);
        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) => inner.AdvanceTo(consumed, examined);
        public override void CancelPendingRead()
        {
            if (Interlocked.Exchange(ref _wakeFailure, null) is { } failure)
            {
                Interlocked.Increment(ref _failedWakes);
                WakeFailed.TrySetResult();
                throw failure;
            }
            inner.CancelPendingRead();
        }
        public override void Complete(Exception? exception = null) => inner.Complete(exception);
        public override ValueTask CompleteAsync(Exception? exception = null) => inner.CompleteAsync(exception);
    }
}
