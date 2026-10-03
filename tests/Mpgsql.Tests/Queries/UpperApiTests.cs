using System.Buffers;
using System.Text;
using Mpgsql.Converters;
using Mpgsql.Protocol;
using Mpgsql.Tests.Converters;
using Mpgsql.Tests.Protocol;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class UpperApiTests
{
    private static MpgsqlDataSource Source(ScriptedSession wire, int inFlight = 8, long rowBytes = 8 * 1024 * 1024,
        Func<MpgsqlMessageSession, CancellationToken, ValueTask>? cancel = null, TimeSpan? recovery = null)
        => new(_ => ValueTask.FromResult(wire.Session), cancel ?? ((_, _) => ValueTask.CompletedTask), new()
        {
            MaxConnections = 1, MaxInFlightPerConnection = inFlight,
            MaxBufferedRowBytesPerConnection = rowBytes, RecoveryTimeout = recovery ?? TimeSpan.FromSeconds(5)
        });

    private static async Task<byte[]> ThroughSync(ScriptedSession wire, int count = 1)
    {
        var all = new List<byte>();
        while (Tags([.. all]).Count(t => t == 'S') < count) all.AddRange(await wire.ReadOutputAsync());
        return [.. all];
    }

    private static byte[] NonQuery(string tag) => Join(Packet('1'), Packet('2'), Packet('n'), Command(tag));

    [Fact]
    public async Task EmptyZeroColumnRowsetIsNotMistakenForNoData()
    {
        await using var wire = new ScriptedSession(); await using var source = Source(wire);
        await using var connection = await source.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var batch = connection.CreateBatch();
        batch.Commands.Add(batch.CreateCommand("select from empty_table")); batch.Commands.Add(batch.CreateCommand("select 2::bigint"));
        var scalar = batch.ExecuteScalarAsync<long>(TestContext.Current.CancellationToken).AsTask();
        await ThroughSync(wire); await wire.WriteAsync(Join(Begin(), Command("SELECT 0"), Query(2), Ready()));
        Assert.False((await scalar).HasRow);
    }

    [Fact]
    public async Task MultiplexedRequestsPublishDistinctSyncGroupsBeforeFirstResponse()
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire);
        var first = source.ExecuteReaderAsync("select 1::bigint", cancellationToken: TestContext.Current.CancellationToken).AsTask();
        var second = source.ExecuteReaderAsync("select 2::bigint", cancellationToken: TestContext.Current.CancellationToken).AsTask();
        Assert.Equal("PBDESPBDES", new string(Tags(await ThroughSync(wire, 2))));
        var writing = wire.WriteAsync(Join(Query(1), Ready(), Query(2), Ready()));
        await using var b = await second.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        await using var a = await first.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.True(await b.ReadAsync()); Assert.Equal(2, b.GetFieldValue<long>(0));
        Assert.True(await a.ReadAsync()); Assert.Equal(1, a.GetFieldValue<long>(0));
        Assert.False(await b.NextResultAsync()); Assert.False(await a.NextResultAsync());
        await writing;
    }

    [Fact]
    public async Task AdmissionWaitsForReaderLifetimeAndCanBeCancelledWithoutSending()
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire, inFlight: 1);
        var opening = source.ExecuteReaderAsync("select 1::bigint", cancellationToken: TestContext.Current.CancellationToken).AsTask();
        await ThroughSync(wire);
        await wire.WriteAsync(Join(Query(1), Ready()));
        var reader = await opening;
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var waiting = source.ExecuteReaderAsync("select 2::bigint", cancellationToken: cancelled.Token).AsTask();
        Assert.False(waiting.IsCompleted);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
        Assert.False(wire.HasOutput());
        await reader.DisposeAsync();
        var next = source.ExecuteScalarAsync<long>("select 3::bigint", cancellationToken: TestContext.Current.CancellationToken).AsTask();
        await ThroughSync(wire);
        await wire.WriteAsync(Join(Query(3), Ready()));
        Assert.Equal(3, (await next).Value);
    }

    [Fact]
    public async Task CommandOwnershipSingleUseAndFrozenSettings()
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire);
        await using var connection = await source.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand("select $1");
        command.Parameters.Add(MpgsqlParameter.Int64(9));
        var opening = command.ExecuteReaderAsync(TestContext.Current.CancellationToken).AsTask();
        await ThroughSync(wire);
        Assert.Throws<InvalidOperationException>(() => command.CommandText = "select 2");
        Assert.Throws<InvalidOperationException>(() => command.Parameters.Add(MpgsqlParameter.Int64(2)));
        await using var overlap = connection.CreateCommand("select 2");
        await Assert.ThrowsAsync<InvalidOperationException>(() => overlap.ExecuteReaderAsync(TestContext.Current.CancellationToken).AsTask());
        await wire.WriteAsync(Join(Query(9), Ready()));
        await using var reader = await opening;
        Assert.True(await reader.ReadAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => overlap.ExecuteReaderAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.False(await reader.NextResultAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => command.ExecuteReaderAsync(TestContext.Current.CancellationToken).AsTask());
        await reader.DisposeAsync(); await command.DisposeAsync(); await command.DisposeAsync();
    }

    [Fact]
    public async Task BatchUsesSameCommandsAndPreventsConflictingOwnership()
    {
        await using var wire = new ScriptedSession();
        await using var otherWire = new ScriptedSession();
        await using var source = Source(wire);
        await using var otherSource = Source(otherWire);
        await using var connection = await source.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var other = await otherSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var batch = connection.CreateBatch();
        var first = batch.CreateCommand("select 1::bigint");
        Assert.Empty(batch.Commands);
        batch.Commands.Add(first);
        Assert.Throws<InvalidOperationException>(() => batch.Commands.Add(first));
        await using var foreign = other.CreateCommand("select 2");
        Assert.Throws<ArgumentException>(() => batch.Commands.Add(foreign));
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.ExecuteReaderAsync(TestContext.Current.CancellationToken).AsTask());
        var second = connection.CreateCommand("select 2::bigint"); batch.Commands.Add(second);
        var opening = batch.ExecuteReaderAsync(TestContext.Current.CancellationToken).AsTask();
        Assert.Throws<InvalidOperationException>(() => batch.Commands.Clear());
        Assert.Throws<InvalidOperationException>(() => second.CommandText = "select 3");
        Assert.Equal("PBDEPBDES", new string(Tags(await ThroughSync(wire))));
        await wire.WriteAsync(Join(Query(1), Query(2), Ready()));
        await using var reader = await opening;
        Assert.True(await reader.ReadAsync()); Assert.Equal(1, reader.GetInt64(0));
        Assert.True(await reader.NextResultAsync()); Assert.Equal(1, reader.QueryIndex);
        Assert.True(await reader.ReadAsync()); Assert.Equal(2, reader.GetInt64(0));
        Assert.False(await reader.NextResultAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => batch.ExecuteReaderAsync(TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task ScalarDistinguishesNoRowNullAndValueAndDoesNotSearchLaterRowsets()
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire);
        byte[][] answers = [Join(Begin(20), Command("SELECT 0"), Ready()),
            Join(Begin(20), Row((byte[]?)null), Command(), Ready()), Join(Query(42), Ready())];
        for (int i = 0; i < answers.Length; i++)
        {
            var resultTask = source.ExecuteScalarAsync<long>("select value", cancellationToken: TestContext.Current.CancellationToken).AsTask();
            await ThroughSync(wire); await wire.WriteAsync(answers[i]);
            var result = await resultTask;
            Assert.Equal(i != 0, result.HasRow); Assert.Equal(i == 1, result.IsNull);
            if (i == 2) Assert.Equal(42, result.Value);
            else Assert.Throws<InvalidOperationException>(() => result.Value);
        }
        await using var connection = await source.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var batch = connection.CreateBatch();
        batch.Commands.Add(batch.CreateCommand("update t"));
        batch.Commands.Add(batch.CreateCommand("select empty"));
        batch.Commands.Add(batch.CreateCommand("select 7::bigint"));
        var scalar = batch.ExecuteScalarAsync<long>(TestContext.Current.CancellationToken).AsTask();
        await ThroughSync(wire);
        await wire.WriteAsync(Join(NonQuery("UPDATE 1"), Begin(20), Command("SELECT 0"), Query(7), Ready()));
        Assert.False((await scalar).HasRow);
    }

    [Fact]
    public async Task ScalarObservesErrorAfterItsFirstValueAndConnectionCanRecover()
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire);
        await using var connection = await source.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var batch = connection.CreateBatch();
        batch.Commands.Add(batch.CreateCommand("select 7::bigint")); batch.Commands.Add(batch.CreateCommand("bad sql"));
        var scalar = batch.ExecuteScalarAsync<long>(TestContext.Current.CancellationToken).AsTask();
        await ThroughSync(wire);
        await wire.WriteAsync(Query(7));
        Assert.False(scalar.IsCompleted);
        await wire.WriteAsync(Join(Error(), Ready()));
        await Assert.ThrowsAsync<MpgsqlServerException>(() => scalar);
        await using var next = connection.CreateCommand("select 8::bigint");
        var result = next.ExecuteScalarAsync<long>(TestContext.Current.CancellationToken).AsTask();
        await ThroughSync(wire); await wire.WriteAsync(Join(Query(8), Ready()));
        Assert.Equal(8, (await result).Value);
    }

    [Fact]
    public async Task NonQueryCountsDmlAndIgnoresSelectAndOtherTags()
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire);
        await using var connection = await source.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var batch = connection.CreateBatch();
        foreach (string sql in new[] { "insert", "update", "delete", "merge", "select", "create" })
            batch.Commands.Add(batch.CreateCommand(sql));
        var task = batch.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).AsTask();
        await ThroughSync(wire);
        await wire.WriteAsync(Join(NonQuery("INSERT 0 2"), NonQuery("UPDATE 3"), NonQuery("DELETE 4"),
            NonQuery("MERGE 5"), Query(99), NonQuery("CREATE TABLE"), Ready()));
        Assert.Equal(14, await task);
        var select = source.ExecuteNonQueryAsync("select 1::bigint", cancellationToken: TestContext.Current.CancellationToken).AsTask();
        // Return the explicit lease before asking the same transport to multiplex.
        await connection.DisposeAsync(); await ThroughSync(wire); await wire.WriteAsync(Join(Query(1), Ready()));
        Assert.Equal(-1, await select);
    }

    [Fact]
    public async Task JsonbGettersUseOwnedMemoryAndPreserveNullability()
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire);
        var task = source.ExecuteReaderAsync("select jsonb_values", cancellationToken: TestContext.Current.CancellationToken).AsTask();
        await ThroughSync(wire);
        byte[] scalar = TestWire.Bytes("017b2278223a22d0aff09f9880227d");
        byte[] array = ConverterAssertions.ArrayBytes(3802, scalar, scalar);
        byte[] nullableArray = ConverterAssertions.ArrayBytes(3802, scalar, null);
        await wire.WriteAsync(Join(Begin(3802, 3802, 3807, 3807, 3807, 3807),
            Row(scalar, null, array, nullableArray, null, null), Command(), Ready()));
        await using var reader = await task;
        Assert.True(await reader.ReadAsync());
        var value = reader.GetFieldValue<Memory<byte>>(0);
        Assert.Equal(scalar[1..], value.ToArray());
        Assert.Equal(scalar[1..], reader.GetFieldValue<Memory<byte>?>(0)!.Value.ToArray());
        Assert.Null(reader.GetFieldValue<Memory<byte>?>(1));
        Assert.Throws<InvalidOperationException>(() => reader.GetFieldValue<Memory<byte>>(1));
        Assert.Throws<InvalidCastException>(() => reader.GetFieldValue<string>(0));
        Assert.Throws<InvalidCastException>(() => reader.GetFieldValue<string>(1));
        var values = reader.GetFieldValue<ReadOnlyMemory<Memory<byte>>>(2);
        Assert.Equal(scalar[1..], values.Span[0].ToArray());
        Assert.Equal(scalar[1..], reader.GetFieldValue<ReadOnlyMemory<Memory<byte>>?>(2)!.Value.Span[1].ToArray());
        var nullable = reader.GetFieldValue<ReadOnlyMemory<Memory<byte>?>>(3);
        Assert.Equal(scalar[1..], nullable.Span[0]!.Value.ToArray());
        Assert.Null(nullable.Span[1]);
        Assert.Null(reader.GetFieldValue<ReadOnlyMemory<Memory<byte>?>?>(3)!.Value.Span[1]);
        Assert.Throws<NotSupportedException>(() => reader.GetFieldValue<ReadOnlyMemory<Memory<byte>>>(3));
        Assert.Throws<InvalidCastException>(() => reader.GetFieldValue<ReadOnlyMemory<string?>>(2));
        Assert.Null(reader.GetFieldValue<ReadOnlyMemory<Memory<byte>>?>(4));
        Assert.Null(reader.GetFieldValue<ReadOnlyMemory<Memory<byte>?>?>(5));
        Assert.Throws<InvalidOperationException>(() => reader.GetFieldValue<ReadOnlyMemory<Memory<byte>>>(4));
        Assert.False(await reader.NextResultAsync());
        await reader.DisposeAsync();
        Assert.Equal(scalar[1..], value.ToArray());
        Assert.Equal(scalar[1..], values.Span[0].ToArray());
        Assert.Equal(scalar[1..], nullable.Span[0]!.Value.ToArray());
        value.Span[0] = (byte)'[';
        Assert.Equal((byte)'{', values.Span[0].Span[0]);
    }

    [Fact]
    public async Task GenericGetterValidatesOidAndNullability()
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire);
        var task = source.ExecuteReaderAsync("select values", cancellationToken: TestContext.Current.CancellationToken).AsTask();
        await ThroughSync(wire);
        await wire.WriteAsync(Join(Begin(20, 25, 16), Row(null, Encoding.UTF8.GetBytes("привет"), [1]), Command(), Ready()));
        await using var reader = await task;
        Assert.True(await reader.ReadAsync()); Assert.True(reader.IsDBNull(0));
        Assert.Null(reader.GetFieldValue<long?>(0));
        Assert.Throws<InvalidOperationException>(() => reader.GetFieldValue<long>(0));
        Assert.Throws<InvalidCastException>(() => reader.GetFieldValue<int?>(0));
        Assert.Equal("привет", reader.GetFieldValue<string>(1)); Assert.True(reader.GetFieldValue<bool>(2));
        Assert.False(await reader.NextResultAsync());
    }

    [Fact]
    public async Task LogicalCancellationReturnsPromptlyAndKeepsNeighbourReadable()
    {
        await using var wire = new ScriptedSession();
        int cancels = 0;
        await using var source = Source(wire, cancel: (_, _) => { Interlocked.Increment(ref cancels); return ValueTask.CompletedTask; });
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var cancelled = source.ExecuteReaderAsync("select slow", cancellationToken: cancellation.Token).AsTask();
        var neighbour = source.ExecuteScalarAsync<long>("select 8::bigint", cancellationToken: TestContext.Current.CancellationToken).AsTask();
        await ThroughSync(wire, 2); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
        Assert.False(neighbour.IsCompleted); Assert.False(wire.Session.Completion.IsCompleted); Assert.Equal(0, cancels);
        await wire.WriteAsync(Join(Query(1), Ready(), Query(8), Ready()));
        Assert.Equal(8, (await neighbour).Value);
    }

    [Fact]
    public async Task ExplicitCancellationWaitsForCancelChannelBeforeReleasingConnection()
    {
        await using var wire = new ScriptedSession();
        var cancelStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var source = Source(wire, cancel: async (_, token) =>
        { cancelStarted.TrySetResult(); await cancelClosed.Task.WaitAsync(token); });
        await using var connection = await source.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand("select slow");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var opening = command.ExecuteReaderAsync(cancellation.Token).AsTask();
        await ThroughSync(wire); cancellation.Cancel();
        await cancelStarted.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        await wire.WriteAsync(Join(Error("57014"), Ready()));
        await using var next = connection.CreateCommand("select 2");
        await Assert.ThrowsAsync<InvalidOperationException>(() => next.ExecuteReaderAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.False(opening.IsCompleted);
        cancelClosed.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
        var succeeding = next.ExecuteScalarAsync<long>(TestContext.Current.CancellationToken).AsTask();
        await ThroughSync(wire); await wire.WriteAsync(Join(Query(2), Ready()));
        Assert.Equal(2, (await succeeding).Value);
        await Assert.ThrowsAsync<InvalidOperationException>(() => command.ExecuteReaderAsync(TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task CancellationBeforePublicationDoesNotSendSqlOrServerCancel()
    {
        await using var wire = new ScriptedSession();
        int cancels = 0;
        await using var source = Source(wire, cancel: (_, _) => { cancels++; return ValueTask.CompletedTask; });
        await using var connection = await source.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand("select 1");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteReaderAsync(cancellation.Token).AsTask());
        Assert.False(wire.HasOutput()); Assert.Equal(0, cancels);
    }

    [Fact]
    public async Task RowBudgetAllowsOneOversizedRowAndUnblocksOnConsumption()
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire, rowBytes: 8);
        var opening = source.ExecuteReaderAsync("select large", cancellationToken: TestContext.Current.CancellationToken).AsTask();
        await ThroughSync(wire);
        byte[] payload = new byte[2000]; payload[0] = 7;
        var writing = wire.WriteAsync(Join(Begin(17), Row(payload), Row(payload), Command("SELECT 2"), Ready()), fragment: 11);
        await using var reader = await opening.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync());
        Assert.InRange(wire.Session.BufferedRowBytes, 2006, 2006);
        Assert.Equal(7, reader.GetFieldValue<ReadOnlyMemory<byte>>(0).Span[0]);
        Assert.True(await reader.ReadAsync()); Assert.Equal(2006, wire.Session.BufferedRowBytes);
        Assert.False(await reader.NextResultAsync()); await writing;
        Assert.Equal(0, wire.Session.BufferedRowBytes);
    }

    [Fact]
    public async Task EarlyReaderDisposeDrainsRowsAndContinuesSubmittingBatch()
    {
        await using var wire = new ScriptedSession(blockWrites: true);
        await using var source = Source(wire, rowBytes: 14);
        await using var connection = await source.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var batch = connection.CreateBatch();
        batch.Commands.Add(batch.CreateCommand("select first"));
        var second = batch.CreateCommand("select $1"); second.Parameters.Add(MpgsqlParameter.Text(new string('x', 10000)));
        batch.Commands.Add(second);
        var opening = batch.ExecuteReaderAsync(TestContext.Current.CancellationToken).AsTask();
        var firstWrite = await wire.Outgoing.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("PBDE", new string(Tags(firstWrite.Buffer.ToArray())));
        var writing = wire.WriteAsync(Join(Begin(20), Row(Int64(1)), Row(Int64(2)), Command("SELECT 2")));
        var reader = await opening.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        var disposing = reader.DisposeAsync().AsTask();
        Assert.False(disposing.IsCompleted);
        wire.Outgoing.Reader.AdvanceTo(firstWrite.Buffer.End);
        Assert.Equal("PBDES", new string(Tags(await ThroughSync(wire))));
        await writing; await wire.WriteAsync(Join(Query(3), Ready()));
        await disposing.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.Equal(0, wire.Session.BufferedRowBytes);
    }

    [Fact]
    public async Task ConnectionReturnSendsNoCleanupAndSourceDisposalWakesWaitingRent()
    {
        await using var wire = new ScriptedSession();
        var source = Source(wire);
        var first = await source.OpenConnectionAsync(TestContext.Current.CancellationToken);
        var waiting = source.OpenConnectionAsync(TestContext.Current.CancellationToken).AsTask();
        Assert.False(waiting.IsCompleted); await first.DisposeAsync();
        var second = await waiting.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.False(wire.HasOutput());
        var third = source.OpenConnectionAsync(TestContext.Current.CancellationToken).AsTask();
        await source.DisposeAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => third.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
        await second.DisposeAsync(); await source.DisposeAsync();
    }

    [Fact]
    public async Task CleanupUsesExplicitMaskAndOneSyncWithoutDeallocation()
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire);
        await using var connection = await source.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await connection.ClearSessionStateAsync(MpgsqlSessionCleanup.None, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => connection.ClearSessionStateAsync((MpgsqlSessionCleanup)32, TestContext.Current.CancellationToken).AsTask());
        Assert.False(wire.HasOutput());
        var cleaning = connection.ClearSessionStateAsync(MpgsqlSessionCleanup.Settings | MpgsqlSessionCleanup.ListenSubscriptions
            | MpgsqlSessionCleanup.AdvisoryLocks | MpgsqlSessionCleanup.Cursors | MpgsqlSessionCleanup.TemporaryObjects, TestContext.Current.CancellationToken).AsTask();
        byte[] output = await ThroughSync(wire);
        Assert.Equal("PBDEPBDEPBDEPBDEPBDES", new string(Tags(output)));
        string text = Encoding.UTF8.GetString(output);
        foreach (string sql in new[] { "RESET ALL", "UNLISTEN *", "SELECT pg_catalog.pg_advisory_unlock_all()", "CLOSE ALL", "DISCARD TEMP" }) Assert.Contains(sql, text);
        Assert.DoesNotContain("DEALLOCATE", text); Assert.DoesNotContain("DISCARD ALL", text); Assert.DoesNotContain("DISCARD PLANS", text);
        await wire.WriteAsync(Join(NonQuery("RESET"), NonQuery("UNLISTEN"), Begin(2278), Row(new byte[0]), Command(),
            NonQuery("CLOSE CURSOR"), NonQuery("DISCARD TEMP"), Ready()));
        await cleaning;
    }

    [Fact]
    public async Task EmptyBatchCompletesWithSyncAndNoRows()
    {
        await using var wire = new ScriptedSession(); await using var source = Source(wire);
        await using var connection = await source.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var batch = connection.CreateBatch();
        var result = batch.ExecuteScalarAsync<long>(TestContext.Current.CancellationToken).AsTask();
        Assert.Equal("S", new string(Tags(await ThroughSync(wire))));
        await wire.WriteAsync(Ready()); Assert.False((await result).HasRow);
    }
}
