using System.Buffers;
using Mpgsql.Internal;
using Mpgsql.Multiplexing.Internal;
using Mpgsql.Protocol;
using QueryExecution = Mpgsql.Multiplexing.Internal.QueryExecution;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class PackedExecutionTests
{
    private const string Sql = "select $1::bigint";

    [Theory, InlineData(1), InlineData(16), InlineData(257)]
    public async Task AtomicExecutionKeepsCompleteWireIndicesAndFollowingRawBoundary(int count)
    {
        await using var wire = new ScriptedSession(true);
        await using var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        var queries = new QueryDefinition[count];
        var expected = new ArrayBufferWriter<byte>();
        for (var i = 0;
             i < count;
             i++)
        {
            queries[i] = new QueryDefinition
            (
                Sql, new[]
                {
                    MpgsqlParameterValue.Int64(i + 1)
                }
            );
            ExpectedQuery(expected, i + 1);
        }
        FrontendMessage
            .Sync()
            .Write(expected);
        var work = wire.Session.SendExecution(batch, default, queries);
        await using var following = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        var next = following
            .SendQueryAsync
            (
                Sql, new[]
                {
                    MpgsqlParameterValue.Int64(999)
                }
            )
            .AsTask();
        var sync = following
            .SendSyncAsync()
            .AsTask();
        ExpectedQuery(expected, 999);
        FrontendMessage
            .Sync()
            .Write(expected);
        Assert.Equal(expected.WrittenSpan.ToArray(), await ThroughSyncAsync(wire, 2));
        await Task
            .WhenAll(work.Completion, next, sync)
            .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.True(batch.Sealed.IsCompletedSuccessfully);

        var responses = new byte[count + 3][];
        for (var i = 0;
             i < count;
             i++)
        {
            responses[i] = Query(i + 1);
        }
        responses[count] = Ready();
        responses[count + 1] = Query(999);
        responses[count + 2] = Ready();
        await wire.WriteAsync(Join(responses), 3);
        await using var reader = await batch.ReadResultsAsync();
        for (var i = 0;
             i < count;
             i++)
        {
            Assert.Equal(i, reader.QueryIndex);
            Assert.True(await reader.ReadAsync());
            Assert.Equal(i + 1, reader.GetInt64(0));
            Assert.False(await reader.ReadAsync());
            Assert.Equal(i + 1 < count, await reader.NextResultAsync());
        }
        await batch.Completion.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        await using var nextReader = await following.ReadResultsAsync();
        Assert.True(await nextReader.ReadAsync());
        Assert.Equal(999, nextReader.GetInt64(0));
        Assert.False(await nextReader.NextResultAsync());
        await following.Completion.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.True(wire.Session.IsIdleAndHealthy);
        Assert.Equal(0, wire.Session.BufferedRowBytes);
    }

    [Fact]
    public async Task CancelDuringEncodingWaitsForBorrowedInputsThenDrainsItsSync()
    {
        using var first = new BlockingInputMemory();
        using var blocked = new BlockingInputMemory(true);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var wire = new ScriptedSession();
        await using var source = Source(wire);
        var pooled = new PooledSession(wire.Session)
        {
            Active = 1
        };
        var execution = new QueryExecution
        (
            source, pooled,
            [
                new QueryDefinition
                (
                    "select $1::bigint[]", new[]
                    {
                        MpgsqlParameterValue.Int64Array(first.Memory)
                    }
                ),
                new QueryDefinition
                (
                    "select $1::bigint[]", new[]
                    {
                        MpgsqlParameterValue.Int64Array(blocked.Memory)
                    }
                )
            ], request.Token
        );
        var opening = execution
            .OpenReaderAsync(true)
            .AsTask();
        await blocked.Entered.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        try
        {
            request.Cancel();
            Assert.False(opening.IsCompleted);
        }
        finally { blocked.Resume(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>
        (() => opening.WaitAsync
            (
                TestTimeout,
                TestContext.Current.CancellationToken
            )
        );
        first.Revoke();
        blocked.Revoke();
        var tags = Tags(await ThroughSyncAsync(wire));
        var published = tags.Count(tag => tag == 'E');
        Assert.InRange(published, 0, 1);
        Assert.Equal(1, tags.Count(tag => tag == 'S'));
        await wire.WriteAsync(published == 0 ? Ready() : Join(Query(11), Ready()));
        await execution
            .FinishAsync(true)
            .AsTask()
            .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.Equal(0, pooled.Active);
        Assert.Equal(1, first.Reads);
        Assert.Equal(1, blocked.Reads);
        await FollowingQueryAsync(wire);
    }

    [Fact]
    public async Task TransportFailureStopsBeforeTheNextBorrowedInputAndWaitsForEncoder()
    {
        using var active = new BlockingInputMemory(true);
        using var queued = new BlockingInputMemory();
        await using var wire = new ScriptedSession();
        await using var source = ClientSource(wire);
        await using var connection = await source.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var batch = connection.CreateBatch();
        ArrayCommand(batch, active.Memory);
        ArrayCommand(batch, queued.Memory);
        // Failure is observed by the concurrent input owner while the background
        // encoder is blocked; select its existing large-group duplex path explicitly.
        batch.BatchCommands[0].CommandText += " /*" + new string('x', 64 * 1024) + "*/";
        var opening = batch
            .ExecuteReaderValueTaskAsync(cancellationToken: TestContext.Current.CancellationToken)
            .AsTask();
        await active.Entered.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        try
        {
            await wire.Incoming.Writer.CompleteAsync();
            await Assert.ThrowsAnyAsync<IOException>
            (() => wire.Session.Completion.WaitAsync
                (
                    TestTimeout,
                    TestContext.Current.CancellationToken
                )
            );
            Assert.False(opening.IsCompleted);
        }
        finally { active.Resume(); }
        await Assert.ThrowsAnyAsync<MpgsqlException>
        (() => opening.WaitAsync
            (
                TestTimeout,
                TestContext.Current.CancellationToken
            )
        );
        active.Revoke();
        queued.Revoke();
        Assert.Equal(1, active.Reads);
        Assert.Equal(0, queued.Reads);
        Assert.False(wire.Session.IsHealthy);
        Assert.Equal(0, wire.Session.BufferedRowBytes);
    }

    private static MpgsqlDataSource ClientSource(ScriptedSession wire)
    {
        return new MpgsqlDataSource
        (
            _ => ValueTask.FromResult(wire.Session),
            (_, _) => ValueTask.CompletedTask, new MpgsqlDataSourceOptions
            {
                MaxConnections = 1
            }
        );
    }
    private static MpgsqlMultiplexingDataSource Source(ScriptedSession wire)
    {
        return new MpgsqlMultiplexingDataSource
        (
            _ => ValueTask.FromResult(wire.Session), new MpgsqlMultiplexingOptions
            {
                MaxConnections = 1
            }
        );
    }

    private static void ArrayCommand(MpgsqlBatch batch, Memory<long> values)
    {
        var command = new MpgsqlBatchCommand("select $1::bigint[]");
        command.Parameters.Add(MpgsqlParameterValue.Int64Array(values));
        batch.BatchCommands.Add(command);
    }

    private static void ExpectedQuery(ArrayBufferWriter<byte> output, long value)
    {
        FrontendMessage
            .Parse
            (
                Sql, parameterTypes: new uint[]
                {
                    20
                }
            )
            .Write(output);
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
            .Write(output);
        FrontendMessage
            .Describe(StatementOrPortal.Portal)
            .Write(output);
        FrontendMessage
            .Execute()
            .Write(output);
    }

    private static async Task<byte[]> ThroughSyncAsync(ScriptedSession wire, int count = 1)
    {
        var bytes = new List<byte>();
        while (Tags([.. bytes])
                   .Count(tag => tag == 'S') < count)
        {
            bytes.AddRange(await wire.ReadOutputAsync());
        }
        return [.. bytes];
    }

    private static async Task FollowingQueryAsync(ScriptedSession wire)
    {
        await using var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await batch.SendQueryAsync
        (
            Sql, new[]
            {
                MpgsqlParameterValue.Int64(777)
            }
        );
        await batch.SendSyncAsync();
        await ThroughSyncAsync(wire);
        await wire.WriteAsync(Join(Query(777), Ready()));
        await using var reader = await batch.ReadResultsAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(777, reader.GetInt64(0));
        Assert.False(await reader.NextResultAsync());
        await batch.Completion.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.True(wire.Session.IsIdleAndHealthy);
        Assert.Equal(0, wire.Session.BufferedRowBytes);
    }
}
