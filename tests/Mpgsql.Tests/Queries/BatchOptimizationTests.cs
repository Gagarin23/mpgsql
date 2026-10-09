using System.Buffers;
using Mpgsql.Internal;
using Mpgsql.Multiplexing.Internal;
using Mpgsql.Protocol;
using QueryExecution = Mpgsql.Multiplexing.Internal.QueryExecution;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class BatchOptimizationTests
{
    private static MpgsqlMultiplexingDataSource Source(ScriptedSession wire)
    {
        return new MpgsqlMultiplexingDataSource
        (
            _ => ValueTask.FromResult(wire.Session),
            new MpgsqlMultiplexingOptions
            {
                MaxConnections = 1,
                MaxInFlightPerConnection = 2
            }
        );
    }

    private static async Task<byte[]> ThroughSync(ScriptedSession wire)
    {
        var bytes = new List<byte>();
        while (!Tags([.. bytes])
                   .Contains('S'))
        {
            bytes.AddRange(await wire.ReadOutputAsync());
        }
        return [.. bytes];
    }

    [Fact]
    public async Task AllCommandsAreValidatedBeforeAnyFrameIsPublished()
    {
        await using var wire = new ScriptedSession();
        await using var source = new MpgsqlDataSource(_ => ValueTask.FromResult(wire.Session), (_, _) => ValueTask.CompletedTask);
        await using var connection = await source.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var batch = connection.CreateBatch();
        batch.BatchCommands.Add(new MpgsqlBatchCommand("select 1"));
        var invalid = new MpgsqlBatchCommand("select $1");
        invalid.Parameters.Add(new MpgsqlParameter());
        batch.BatchCommands.Add(invalid);
        await Assert.ThrowsAsync<InvalidOperationException>
        (() => batch
            .ExecuteReaderValueTaskAsync
            (
                cancellationToken:
                TestContext.Current.CancellationToken
            )
            .AsTask()
        );
        Assert.False(wire.HasOutput());
        await using var next = connection.CreateCommand("select 8::bigint");
        var result = next
            .ExecuteScalarAsync<long>(TestContext.Current.CancellationToken)
            .AsTask();
        Assert.Equal("PBDES", new string(Tags(await ThroughSync(wire))));
        await wire.WriteAsync(Join(Query(8), Ready()), 1);
        Assert.Equal(8, (await result.WaitAsync(TestTimeout, TestContext.Current.CancellationToken)).Value);
        Assert.True(wire.Session.IsHealthy);
        Assert.Equal(0, wire.Session.BufferedRowBytes);
    }

    [Fact]
    public async Task ParameterCollectionKeepsOrderingNullEmptyAndFrozenMutators()
    {
        await using var wire = new ScriptedSession();
        await using var source = new MpgsqlDataSource(_ => ValueTask.FromResult(wire.Session), (_, _) => ValueTask.CompletedTask);
        await using var connection = await source.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand("select $1, $2, $3");
        var parameters = command.Parameters;
        var value = MpgsqlParameter.Int64(11);
        var nil = MpgsqlParameter.Int64(null);
        parameters.Add(value);
        parameters.Add(nil);
        parameters.Add(MpgsqlParameter.Int64Array(ReadOnlyMemory<long>.Empty));
        Assert.Equal(3, parameters.Count);
        Assert.Equal(1, parameters.IndexOf(nil));
        Assert.True(parameters.Contains(value));
        var copied = new MpgsqlParameter[5];
        parameters.CopyTo(copied, 1);
        Assert.Same(value, copied[1]);
        Assert.Same(nil, copied[2]);

        var opening = command
            .ExecuteReaderValueTaskAsync(cancellationToken: TestContext.Current.CancellationToken)
            .AsTask();
        Assert.Throws<InvalidOperationException>(() => parameters.Clear());
        Assert.Throws<InvalidOperationException>(() => parameters.RemoveAt(0));
        Assert.Throws<InvalidOperationException>(() => parameters[0] = MpgsqlParameter.Int64(7));
        var expected = new ArrayBufferWriter<byte>();
        FrontendMessage
            .Parse
            (
                command.CommandText, parameterTypes: new uint[]
                {
                    20,
                    20,
                    1016
                }
            )
            .Write(expected);
        FrontendMessage
            .Bind
            (
                parameters: new ReadOnlyMemory<byte>?[]
                {
                    Int64(11),
                    null,
                    new byte[]
                    {
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        20
                    }
                },
                parameterFormats: new[]
                {
                    FormatCode.Binary
                }, resultFormats: new[]
                {
                    FormatCode.Binary
                }
            )
            .Write(expected);
        FrontendMessage
            .Describe(StatementOrPortal.Portal)
            .Write(expected);
        FrontendMessage
            .Execute()
            .Write(expected);
        FrontendMessage
            .Sync()
            .Write(expected);
        Assert.Equal(expected.WrittenSpan.ToArray(), await ThroughSync(wire));
        var writing = wire.WriteAsync(Join(Query(11), Ready()));
        await using var reader = await opening.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(11, reader.GetInt64(0));
        Assert.False(await reader.NextResultAsync(TestContext.Current.CancellationToken));
        await writing;
        await command.DisposeAsync();
        Assert.Empty(parameters);
    }

    [Fact]
    public async Task PublicBatchDisposeWaitsForEveryEncoderBeforeClearingParameters()
    {
        using var input = new BlockingInputMemory(true);
        await using var wire = new ScriptedSession();
        await using var source = new MpgsqlDataSource(_ => ValueTask.FromResult(wire.Session), (_, _) => ValueTask.CompletedTask);
        await using var connection = await source.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var batch = connection.CreateBatch();
        // This test coordinates a background encoder and disposal. Keep that
        // duplex path selected rather than blocking its own thread in inline encoding.
        var first = new MpgsqlBatchCommand("select $1::bigint[] /*" + new string('x', 64 * 1024) + "*/");
        first.Parameters.Add(MpgsqlParameterValue.Int64Array(input.Memory));
        batch.BatchCommands.Add(first);
        var second = new MpgsqlBatchCommand("select $1::bigint");
        second.Parameters.Add(MpgsqlParameterValue.Int64(22));
        batch.BatchCommands.Add(second);
        var opening = batch
            .ExecuteReaderValueTaskAsync(cancellationToken: TestContext.Current.CancellationToken)
            .AsTask();
        await input.Entered.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Task disposing;
        try
        {
            disposing = batch
                .DisposeAsync()
                .AsTask();
            Assert.False(disposing.IsCompleted);
            Assert.Single(first.Parameters);
            Assert.Single(second.Parameters);
            Assert.Throws<InvalidOperationException>(() => first.Parameters.Clear());
        }
        finally { input.Resume(); }
        Assert.Equal("PBDEPBDES", new string(Tags(await ThroughSync(wire))));
        await wire.WriteAsync(Join(Query(11), Query(22), Ready()), 3);
        await disposing.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ObjectDisposedException>
        (() => opening.WaitAsync
            (
                TestTimeout,
                TestContext.Current.CancellationToken
            )
        );
        Assert.Equal(1, input.Reads);
        input.Revoke();
        Assert.Empty(first.Parameters);
        Assert.Empty(second.Parameters);
        Assert.True(wire.Session.IsHealthy);
        Assert.Equal(0, wire.Session.BufferedRowBytes);
    }

    [Fact]
    public async Task GroupCancellationWaitsForEncodingButNotBlockedSyncDelivery()
    {
        using var encoding = new BlockingInputMemory(true);
        using var queued = new BlockingInputMemory();
        await using var wire = new ScriptedSession(true);
        await using var source = Source(wire);
        var pooled = new PooledSession(wire.Session)
        {
            Active = 1
        };
        using var request = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var execution = new QueryExecution
        (
            source, pooled,
            [
                new QueryDefinition
                (
                    "select $1::bigint[]", new[]
                    {
                        MpgsqlParameterValue.Int64Array(encoding.Memory)
                    }
                ),
                new QueryDefinition
                (
                    "select $1::bigint[]", new[]
                    {
                        MpgsqlParameterValue.Int64Array(queued.Memory)
                    }
                )
            ], request.Token
        );
        var opening = execution
            .OpenReaderAsync(true)
            .AsTask();
        await encoding.Entered.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        try
        {
            request.Cancel();
            Assert.False(opening.IsCompleted);
            Assert.Equal(1, encoding.Reads);
            Assert.Equal(0, queued.Reads);
        }
        finally { encoding.Resume(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>
        (() => opening.WaitAsync
            (
                TestTimeout,
                TestContext.Current.CancellationToken
            )
        );
        encoding.Revoke();
        queued.Revoke();
        var held = await wire.Outgoing.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("S", new string(Tags(held.Buffer.ToArray())));
        Assert.False
        (
            execution.FinishAsync(true)
                .IsCompleted
        );
        wire.Outgoing.Reader.AdvanceTo(held.Buffer.End);
        await wire.WriteAsync(Ready());
        await execution
            .FinishAsync(true)
            .AsTask()
            .WaitAsync
            (
                TestTimeout,
                TestContext.Current.CancellationToken
            );
        Assert.Equal(0, queued.Reads);
        Assert.Equal(0, pooled.Active);
        Assert.True(wire.Session.IsHealthy);
        Assert.Equal(0, wire.Session.BufferedRowBytes);
    }

    [Fact]
    public async Task GroupTransportFailureWaitsForActiveEncoderAndIgnoresDuplicateAcknowledgements()
    {
        await using var wire = new ScriptedSession();
        await using var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        MpgsqlParameterValue[] parameters = [MpgsqlParameterValue.Int64(7)];
        var encoding = new OutboundWork(batch, [new QueryDefinition("select $1", parameters), new QueryDefinition("select $1", parameters)]);
        var queued = new OutboundWork(batch, [new QueryDefinition("select $1", parameters)]);
        Assert.True(encoding.TryStart());
        var failure = new IOException("failed transport");
        encoding.FailQueued(failure); // must not release an encoder's input
        queued.FailQueued(failure);
        Assert.False(encoding.Completion.IsCompleted);
        Assert.True(encoding.TryGetQuery(out var active));
        Assert.Equal(parameters.Length, active.Parameters.Length);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => queued.Completion));
        encoding.Complete(failure);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => encoding.Completion));
        encoding.Complete();
        queued.Complete();
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => encoding.Completion));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => queued.Completion));
    }
}
