using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Mpgsql.Protocol;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class SharedSyncTests
{
    private const string Sql = "select $1::bigint";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static MpgsqlDataSource ClientSource(
        ScriptedSession wire, int inFlight = 8,
        long rowBytes = 8 * 1024 * 1024, Func<MpgsqlMessageSession, CancellationToken, ValueTask>? cancel = null,
        TimeSpan? recovery = null
    )
    {
        return new MpgsqlDataSource
        (
            _ => ValueTask.FromResult(wire.Session), cancel ?? ((_, _) => ValueTask.CompletedTask),
            new MpgsqlDataSourceOptions
            {
                MaxConnections = 1,
                MaxBufferedRowBytesPerConnection = rowBytes,
                RecoveryTimeout = recovery ?? TimeSpan.FromSeconds(5)
            }
        );
    }

    private static MpgsqlMultiplexingDataSource Source(
        ScriptedSession wire, int size = 4,
        int inFlight = 8,
        int delayMs = 2000, long rowBytes = 64 * 1024
    )
    {
        return new MpgsqlMultiplexingDataSource
        (
            _ => ValueTask.FromResult(wire.Session), new MpgsqlMultiplexingOptions
            {
                MaxConnections = 1,
                MaxInFlightPerConnection = inFlight,
                SyncGroupSize = size,
                SyncGroupTimeout = TimeSpan.FromMilliseconds(delayMs),
                MaxBufferedRowBytesPerConnection = rowBytes
            }
        );
    }

    private static Task<MpgsqlResultReader> Open(
        MpgsqlMultiplexingDataSource source, long value,
        CancellationToken? token = null
    )
    {
        return source
            .ExecuteReaderAsync
            (
                Sql, new[]
                {
                    MpgsqlParameterValue.Int64(value)
                }, token ?? Token
            )
            .AsTask();
    }
    private static Task<MpgsqlScalarResult<long>> Scalar(MpgsqlMultiplexingDataSource source, long value)
    {
        return source
            .ExecuteScalarAsync<long>
            (
                Sql, new[]
                {
                    MpgsqlParameterValue.Int64(value)
                }, Token
            )
            .AsTask();
    }

    private static async Task<byte[]> Through(
        ScriptedSession wire, char tag = 'S',
        int count = 1
    )
    {
        var bytes = new List<byte>();
        while (Tags([.. bytes])
                   .Count(t => t == tag) < count)
        {
            bytes.AddRange(await wire.ReadOutputAsync());
        }
        return [.. bytes];
    }

    private static byte[] Expected(params long[] values)
    {
        var destination = new ArrayBufferWriter<byte>();
        foreach (var value in values)
        {
            FrontendMessage
                .Parse
                (
                    Sql, parameterTypes: new uint[]
                    {
                        20
                    }
                )
                .Write(destination);
            FrontendMessage
                .Bind
                (
                    parameters: new ReadOnlyMemory<byte>?[]
                    {
                        Int64(value)
                    },
                    parameterFormats: new[]
                    {
                        FormatCode.Binary
                    }, resultFormats: new[]
                    {
                        FormatCode.Binary
                    }
                )
                .Write(destination);
            FrontendMessage
                .Describe(StatementOrPortal.Portal)
                .Write(destination);
            FrontendMessage
                .Execute()
                .Write(destination);
        }
        FrontendMessage
            .Sync()
            .Write(destination);
        return destination.WrittenSpan.ToArray();
    }

    private static async Task Following(
        ScriptedSession wire, MpgsqlMultiplexingDataSource source,
        long value = 999
    )
    {
        var count = source.Options.SyncGroupSize <= source.Options.MaxInFlightPerConnection ? source.Options.SyncGroupSize : 1;
        var values = Enumerable
            .Range(0, count)
            .Select(i => value + i)
            .ToArray();
        var next = values
            .Select(v => Scalar(source, v))
            .ToArray();
        Assert.Equal(Expected(values), await Through(wire));
        await wire.WriteAsync(Join([.. values.Select(Query), Ready()]), 3);
        var results = await Task
            .WhenAll(next)
            .WaitAsync(TestTimeout, Token);
        Assert.Equal(values, results.Select(r => r.Value));
        Assert.True(wire.Session.IsIdleAndHealthy);
        Assert.Equal(0, wire.Session.BufferedRowBytes);
    }

    [Fact]
    public async Task CountBoundaryRoutesIndependentReadersAndExactWireBytes()
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire);
        var opens = Enumerable
            .Range(0, 4)
            .Select(i => Open(source, 10 + i))
            .ToArray();
        Assert.Equal(Expected(10, 11, 12, 13), await Through(wire));
        await wire.WriteAsync(Join(Query(10), Query(11), Query(12), Query(13)), 1);
        var readers = await Task
            .WhenAll(opens)
            .WaitAsync(TestTimeout, Token);
        for (var i = 3;
             i >= 0;
             i--)
        {
            Assert.Equal(0, readers[i].QueryIndex);
            Assert.Equal
            (
                (uint)20, readers[i]
                    .Columns.Span[0].DataTypeOid
            );
            Assert.True
            (
                await readers[i]
                    .ReadAsync()
            );
            Assert.Equal
            (
                10 + i, readers[i]
                    .GetInt64(0)
            );
            Assert.False
            (
                await readers[i]
                    .ReadAsync()
            );
        }
        var finishing = readers
            .Select
            (r => r
                .NextResultAsync()
                .AsTask()
            )
            .ToArray();
        Assert.All(finishing, t => Assert.False(t.IsCompleted));
        await wire.WriteAsync(Ready(), 1);
        Assert.All
        (
            await Task
                .WhenAll(finishing)
                .WaitAsync(TestTimeout, Token), Assert.False
        );
        foreach (var reader in readers)
        {
            await reader.DisposeAsync();
        }
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Fact]
    public async Task TimerQueuesSyncWithoutAnotherRequestOrAnyResultConsumption()
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire, 64, 1, 20);
        var opening = Open(source, 42);
        // No backend response or reader is available to the client yet.
        Assert.Equal(Expected(42), await Through(wire));
        Assert.False(opening.IsCompleted);
        await wire.WriteAsync(Join(Query(42), Ready()), 2);
        await using var reader = await opening.WaitAsync(TestTimeout, Token);
        Assert.True
        (
            await reader
                .ReadAsync()
                .AsTask()
        );
        Assert.Equal(42, reader.GetInt64(0));
        Assert.False(await reader.NextResultAsync());
        await Following(wire, source);
    }

    [Fact]
    public async Task TimerClosesGroupWhileReaderHoldsItsFirstRow()
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire, 8, delayMs: 100);
        var opening = Open(source, 17);
        var sent = await Through(wire, 'E');
        await wire.WriteAsync(Join(Begin(20), Row(Int64(17))));
        await using var reader = await opening.WaitAsync(TestTimeout, Token);
        Assert.True
        (
            await reader
                .ReadAsync()
                .AsTask()
        );
        Assert.Equal(17, reader.GetInt64(0));
        if (!Tags(sent)
                .Contains('S'))
        {
            Assert.Equal("S", new string(Tags(await Through(wire))));
        }
        // The client has held the row throughout; no movement/disposal triggered Sync.
        await wire.WriteAsync(Join(Command(), Ready()));
        Assert.False(await reader.NextResultAsync());
        Assert.Equal(0, wire.Session.BufferedRowBytes);
        await Following(wire, source);
    }

    [Theory, InlineData(0), InlineData(1), InlineData(2), InlineData(3)]
    // Parse error.
    // Bind error.
    // Describe error.
    // Error while executing/streaming rows.
    public async Task SqlErrorMarksCompletedAndSkippedRequestsAndRecovers(int confirmations)
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire, 3);
        var first = Scalar(source, 1);
        var failed = Scalar(source, 2);
        var skipped = Scalar(source, 3);
        Assert.Equal(Expected(1, 2, 3), await Through(wire));
        var response = new List<byte[]>
        {
            Query(1)
        };
        if (confirmations >= 1)
        {
            response.Add(Packet('1'));
        }
        if (confirmations >= 2)
        {
            response.Add(Packet('2'));
        }
        if (confirmations >= 3)
        {
            response.Add(Description(20));
            response.Add(Row(Int64(2)));
        }
        response.Add(Error());
        response.Add(Ready());
        await wire.WriteAsync(Join([.. response]), 3);
        var previousError = await Assert.ThrowsAsync<MpgsqlSyncGroupException>(() => first.WaitAsync(TestTimeout, Token));
        Assert.False(previousError.WasSkipped);
        Assert.Equal(1, previousError.FailedRequestIndex);
        var original = await Assert.ThrowsAsync<MpgsqlServerException>(() => failed.WaitAsync(TestTimeout, Token));
        Assert.Equal("22012", original.SqlState);
        Assert.Equal(0, original.QueryIndex);
        Assert.Equal(TransactionStatus.Idle, original.TransactionStatus);
        var skippedError = await Assert.ThrowsAsync<MpgsqlSyncGroupException>(() => skipped.WaitAsync(TestTimeout, Token));
        Assert.True(skippedError.WasSkipped);
        Assert.Same(original, skippedError.Cause);
        Assert.Same(original, previousError.Cause);
        await Following(wire, source);
    }

    [Fact]
    public async Task RequestAdmittedAfterReceivedErrorStartsAfterRecoverySync()
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire, 2);
        var failed = Scalar(source, 1);
        var firstOutput = await Through(wire, 'E');
        Assert.Equal("PBDE", new string(Tags(firstOutput)));
        await wire.WriteAsync(Join(Error(), Packet('N', Encoding.UTF8.GetBytes("SNOTICE\0Mmarker\0\0"))));
        var deadline = Stopwatch.StartNew();
        while (!wire.Session.TryReadNotice(out _))
        {
            Assert.True(deadline.Elapsed < TestTimeout);
            await Task.Delay(1, Token);
        }
        var second = Scalar(source, 2);
        var third = Scalar(source, 3);
        Assert.Equal("SPBDEPBDES", new string(Tags(await Through(wire, count: 2))));
        await wire.WriteAsync(Join(Ready(), Query(2), Query(3), Ready()));
        await Assert.ThrowsAsync<MpgsqlServerException>(() => failed.WaitAsync(TestTimeout, Token));
        Assert.Equal(2, (await second.WaitAsync(TestTimeout, Token)).Value);
        Assert.Equal(3, (await third.WaitAsync(TestTimeout, Token)).Value);
        await Following(wire, source);
    }

    [Fact]
    public async Task QueuedCancellationSkipsOnlyItsQueryAndStillCompletesSharedBoundary()
    {
        await using var wire = new ScriptedSession(true);
        await using var source = Source(wire, 3);
        var first = Open(source, 1);
        var held = await wire.Outgoing.Reader.ReadAsync(Token);
        Assert.Equal("PBDE", new string(Tags(held.Buffer.ToArray())));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var cancelled = Open(source, 2, cancellation.Token);
        var third = Open(source, 3);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(TestTimeout, Token));
        wire.Outgoing.Reader.AdvanceTo(held.Buffer.End);
        Assert.Equal("PBDES", new string(Tags(await Through(wire))));
        await wire.WriteAsync(Join(Query(1), Query(3), Ready()));
        await using var a = await first.WaitAsync(TestTimeout, Token);
        await using var b = await third.WaitAsync(TestTimeout, Token);
        Assert.True(await a.ReadAsync());
        Assert.Equal(1, a.GetInt64(0));
        Assert.False(await a.NextResultAsync());
        Assert.True(await b.ReadAsync());
        Assert.Equal(3, b.GetInt64(0));
        Assert.False(await b.NextResultAsync());
        await Following(wire, source);
    }

    [Fact]
    public async Task CancelledPublishedReaderWakesRowBudgetWithoutDiscardingItsNeighbour()
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire, 2, rowBytes: 32);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var first = Open(source, 1, cancellation.Token);
        var second = Open(source, 2);
        await Through(wire);
        var writing = wire.WriteAsync
        (
            Join
            (
                Begin(20), Row(Int64(1)), Row(Int64(1)), Row(Int64(1)),
                Row(Int64(1)), Command("SELECT 4"), Query(2), Ready()
            ), 1
        );
        await using var reader = await first.WaitAsync(TestTimeout, Token);
        Assert.True
        (
            await reader
                .ReadAsync()
                .AsTask()
        );
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>
        (() => reader
            .ReadAsync()
            .AsTask()
        );
        await reader
            .DisposeAsync()
            .AsTask()
            .WaitAsync(TestTimeout, Token);
        await using var neighbour = await second.WaitAsync(TestTimeout, Token);
        Assert.True(await neighbour.ReadAsync());
        Assert.Equal(2, neighbour.GetInt64(0));
        Assert.False(await neighbour.NextResultAsync());
        await writing.WaitAsync(TestTimeout, Token);
        await Following(wire, source);
    }

    [Fact]
    public async Task EarlyDisposeDrainsOnlyItsRowsAndWaitsForCommonReady()
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire, 2, rowBytes: 32);
        var first = Open(source, 1);
        var second = Open(source, 2);
        await Through(wire);
        await wire.WriteAsync(Join(Begin(20), Row(Int64(1))));
        var reader = await first.WaitAsync(TestTimeout, Token);
        Assert.True
        (
            await reader
                .ReadAsync()
                .AsTask()
        );
        var disposed = reader
            .DisposeAsync()
            .AsTask();
        Assert.False(disposed.IsCompleted);
        await wire.WriteAsync(Join(Row(Int64(1)), Row(Int64(1)), Command("SELECT 3"), Query(2)));
        await using var neighbour = await second.WaitAsync(TestTimeout, Token);
        Assert.True(await neighbour.ReadAsync());
        Assert.Equal(2, neighbour.GetInt64(0));
        Assert.False(await neighbour.ReadAsync());
        Assert.False(disposed.IsCompleted);
        await wire.WriteAsync(Ready());
        Assert.False(await neighbour.NextResultAsync());
        await disposed.WaitAsync(TestTimeout, Token);
        await Following(wire, source);
    }

    [Fact]
    public async Task CancelledRequestRetainsSlotUntilSharedSyncDeliveryAndReady()
    {
        await using var wire = new ScriptedSession(true);
        await using var source = Source(wire, 8, 1, 20);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var opening = Open(source, 1, cancellation.Token);
        Assert.Equal("PBDE", new string(Tags(await Through(wire, 'E'))));
        var sync = await wire.Outgoing.Reader.ReadAsync(Token);
        Assert.Equal("S", new string(Tags(sync.Buffer.ToArray())));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TestTimeout, Token));
        var next = Scalar(source, 2);
        await wire.WriteAsync(Join(Query(1), Ready()));
        Assert.False(next.IsCompleted);
        wire.Outgoing.Reader.AdvanceTo(sync.Buffer.End);
        Assert.Equal(Expected(2), await Through(wire));
        await wire.WriteAsync(Join(Query(2), Ready()));
        Assert.Equal(2, (await next.WaitAsync(TestTimeout, Token)).Value);
        Assert.Equal(0, wire.Session.BufferedRowBytes);
    }

    [Fact]
    public async Task ExplicitConnectionKeepsItsOwnSyncEvenWhenSourceUsesSharedGroups()
    {
        await using var wire = new ScriptedSession();
        await using var source = ClientSource(wire);
        await using (var connection = await source.OpenConnectionAsync(Token))
        {
            await using var command = connection.CreateCommand(Sql);
            command.Parameters.Add(MpgsqlParameterValue.Int64(4));
            var result = command
                .ExecuteScalarAsync<long>(Token)
                .AsTask();
            Assert.Equal(Expected(4), await Through(wire));
            await wire.WriteAsync(Join(Query(4), Ready()));
            Assert.Equal(4, (await result).Value);
            await using var batch = connection.CreateBatch();
            foreach (var value in new long[]
                     {
                         5,
                         6
                     })
            {
                var item = new MpgsqlBatchCommand(Sql);
                item.Parameters.Add(MpgsqlParameterValue.Int64(value));
                batch.BatchCommands.Add(item);
            }
            var scalar = batch
                .ExecuteScalarAsync<long>(Token)
                .AsTask();
            Assert.Equal(Expected(5, 6), await Through(wire));
            await wire.WriteAsync(Join(Query(5), Query(6), Ready()));
            Assert.Equal(5, (await scalar).Value);
        }
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Fact]
    public async Task DisposingSourceAbortsAnOpenTimerGroup()
    {
        await using var wire = new ScriptedSession(true);
        var source = Source(wire, 64);
        var opening = Open(source, 1);
        await Through(wire, 'E');
        await source
            .DisposeAsync()
            .AsTask()
            .WaitAsync(TestTimeout, Token);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => opening.WaitAsync(TestTimeout, Token));
        Assert.False(wire.Session.IsHealthy);
        Assert.Equal(0, wire.Session.BufferedRowBytes);
    }

    [Fact]
    public async Task CountAndTimerRacesKeepFifoValuesAcrossManyGroups()
    {
        const int requests = 128;
        await using var wire = new ScriptedSession();
        await using var source = Source(wire, delayMs: 1);
        var syncs = 0;

        async Task Peer()
        {
            var executed = 0;
            long value = -1;
            while (true)
            {
                var bytes = await wire.ReadOutputAsync();
                var replies = new List<byte[]>();
                for (var offset = 0;
                     offset < bytes.Length;)
                {
                    var length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset + 1));
                    var tag = (char)bytes[offset];
                    if (tag == 'B')
                    {
                        var field = offset + 5;
                        while (bytes[field++] != 0) { } // Portal and statement C strings.
                        while (bytes[field++] != 0) { }
                        int formats = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(field));
                        field += 2 + formats * 2;
                        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(field)));
                        field += 2;
                        Assert.Equal(8, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(field)));
                        field += 4;
                        value = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(field));
                    }
                    else if (tag == 'E')
                    {
                        replies.Add(Query(value));
                        executed++;
                    }
                    else if (tag == 'S')
                    {
                        replies.Add(Ready());
                        syncs++;
                    }
                    offset += length + 1;
                }
                await wire.WriteAsync(Join([.. replies]), 7);
                if (executed == requests && Tags(bytes)
                        .Last() == 'S')
                {
                    break;
                }
            }
        }

        var peer = Task.Run(Peer, Token);
        var workers = Enumerable
            .Range(0, 8)
            .Select
            (async worker =>
                {
                    for (var i = worker;
                         i < requests;
                         i += 8)
                    {
                        if ((i & 7) == 0)
                        {
                            await Task.Delay(2, Token);
                        }
                        var scalar = await Scalar(source, i);
                        Assert.Equal(i, scalar.Value);
                    }
                }
            );
        await Task
            .WhenAll(workers.Append(peer))
            .WaitAsync(TestTimeout, Token);
        Assert.InRange(syncs, requests / 4, requests);
        await Following(wire, source);
    }

    [Theory, InlineData(0, 1), InlineData(1, 0), InlineData(4, -1)]
    public void InvalidSyncSettingsAreRejected(int size, int milliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>
        (() => new MpgsqlMultiplexingOptions
            {
                SyncGroupSize = size,
                SyncGroupTimeout = TimeSpan.FromMilliseconds(milliseconds)
            }.CopyValidated()
        );
    }

    [Theory, InlineData(1), InlineData(64)]
    public async Task TransportFailureFaultsOpenGroupAndFollowingRequestGetsANewSession(int repetitions)
    {
        for (var i = 0;
             i < repetitions;
             i++)
        {
            await VerifyTransportReplacement();
        }
    }

    private static async Task VerifyTransportReplacement()
    {
        await using var broken = new ScriptedSession();
        await using var replacement = new ScriptedSession();
        var factories = 0;
        await using var source = new MpgsqlMultiplexingDataSource
        (
            _ => ValueTask.FromResult
            (
                Interlocked.Increment(ref factories) == 1 ? broken.Session : replacement.Session
            ), new MpgsqlMultiplexingOptions
            {
                MaxConnections = 1,
                SyncGroupSize = 4,
                SyncGroupTimeout = TimeSpan.FromMilliseconds(20)
            }
        );
        var first = Scalar(source, 1);
        var second = Scalar(source, 2);
        await Through(broken, 'E', 2);
        await broken.Incoming.Writer.CompleteAsync();
        await Assert.ThrowsAsync<EndOfStreamException>(() => first.WaitAsync(TestTimeout, Token));
        await Assert.ThrowsAsync<EndOfStreamException>(() => second.WaitAsync(TestTimeout, Token));
        Assert.False(broken.Session.IsHealthy);
        var next = Scalar(source, 3);
        Assert.Equal(Expected(3), await Through(replacement));
        await replacement.WriteAsync(Join(Query(3), Ready()));
        Assert.Equal(3, (await next.WaitAsync(TestTimeout, Token)).Value);
        Assert.Equal(2, factories);
        Assert.True(replacement.Session.IsIdleAndHealthy);
        Assert.Equal(0, broken.Session.BufferedRowBytes);
        Assert.Equal(0, replacement.Session.BufferedRowBytes);
    }

    [Fact]
    public async Task SeparateWriteSourceIsUnaffectedByReadonlyGroupFailure()
    {
        await using var readWire = new ScriptedSession();
        await using var writeWire = new ScriptedSession();
        await using var reads = Source(readWire, 2);
        await using var writes = new MpgsqlMultiplexingDataSource
        (
            _ => ValueTask.FromResult(writeWire.Session), new MpgsqlMultiplexingOptions
            {
                MaxConnections = 1
            }
        );
        var write = writes
            .ExecuteNonQueryAsync("insert into t values (1)", cancellationToken: Token)
            .AsTask();
        Assert.Equal("PBDES", new string(Tags(await Through(writeWire))));
        var failed = Scalar(reads, 1);
        var skipped = Scalar(reads, 2);
        Assert.Equal(Expected(1, 2), await Through(readWire));
        await readWire.WriteAsync(Join(Error(), Ready()));
        await Assert.ThrowsAsync<MpgsqlServerException>(() => failed.WaitAsync(TestTimeout, Token));
        var skip = await Assert.ThrowsAsync<MpgsqlSyncGroupException>(() => skipped.WaitAsync(TestTimeout, Token));
        Assert.True(skip.WasSkipped);
        Assert.False(write.IsCompleted);
        await writeWire.WriteAsync(Join(Packet('1'), Packet('2'), Packet('n'), Command("INSERT 0 1"), Ready()));
        Assert.Equal(1, await write.WaitAsync(TestTimeout, Token));
        Assert.True(writeWire.Session.IsIdleAndHealthy);
        Assert.Equal(0, writeWire.Session.BufferedRowBytes);
        await Following(readWire, reads);
    }
}