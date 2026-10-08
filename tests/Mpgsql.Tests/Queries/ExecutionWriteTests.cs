using System.Buffers;
using System.Buffers.Binary;
using Mpgsql.Internal;
using Mpgsql.Protocol;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class ExecutionWriteTests
{
    private const string Sql = "select $1::bigint";

    private static void ExpectedQuery(ArrayBufferWriter<byte> destination, long value)
    {
        FrontendMessage.Parse(Sql, parameterTypes: new uint[] { 20 }).Write(destination);
        FrontendMessage.Bind(parameters: new ReadOnlyMemory<byte>?[] { Int64(value) },
            parameterFormats: new[] { FormatCode.Binary }, resultFormats: new[] { FormatCode.Binary }).Write(destination);
        FrontendMessage.Describe(StatementOrPortal.Portal).Write(destination);
        FrontendMessage.Execute().Write(destination);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public async Task FailedAdmissionReleasesItsSlotAndLeavesTheNextRequestUsable(int syncGroupSize)
    {
        var token = TestContext.Current.CancellationToken;
        await using var wire = new ScriptedSession();
        var options = new MpgsqlDataSourceOptions
        {
            MaxConnections = 1, MaxInFlightPerConnection = 1,
            SyncGroupSize = syncGroupSize, SyncGroupTimeout = TimeSpan.FromMilliseconds(1)
        };
        await using var source = new MpgsqlDataSource(_ => ValueTask.FromResult(wire.Session),
            (_, _) => ValueTask.CompletedTask, options);
        var pooled = new PooledSession(wire.Session, options) { Active = 1 };
        using var scheduler = pooled.SyncScheduler;
        // The internal producer captures a pre-wire validation failure after slot reservation.
        var execution = new QueryExecution(source, pooled, new(null!, default), token);
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            execution.OpenReaderAsync(waitForInputRelease: true).AsTask().WaitAsync(TestTimeout, token));
        await execution.FinishAsync(discard: true);
        Assert.Equal(0, pooled.Active);
        Assert.False(wire.HasOutput());
        Assert.True(wire.Session.IsIdleAndHealthy);
        Assert.Equal(0, wire.Session.BufferedRowBytes);

        // The same physical transport must accept a complete following boundary.
        pooled.Active = 1;
        var following = new QueryExecution(source, pooled, new(Sql, new[] { MpgsqlParameter.Int64(42) }), token);
        var opening = following.OpenReaderAsync(waitForInputRelease: true).AsTask();
        var output = new List<byte>();
        while (Tags([.. output]).Count(tag => tag == 'S') == 0) output.AddRange(await wire.ReadOutputAsync());
        var expected = new ArrayBufferWriter<byte>(); ExpectedQuery(expected, 42); FrontendMessage.Sync().Write(expected);
        Assert.Equal(expected.WrittenSpan.ToArray(), output.ToArray());
        await wire.WriteAsync(Join(Query(42), Ready()), fragment: 1);
        await using var reader = await opening.WaitAsync(TestTimeout, token);
        Assert.True(await reader.ReadAsync()); Assert.Equal(42, reader.GetInt64(0));
        Assert.False(await reader.NextResultAsync());
        Assert.Equal(0, pooled.Active);
        Assert.True(wire.Session.IsIdleAndHealthy);
        Assert.Equal(0, wire.Session.BufferedRowBytes);
    }

    [Fact]
    public async Task ConcurrentDataSourceAdmissionKeepsEachQueryAndSyncContiguous()
    {
        const int requests = 64;
        var token = TestContext.Current.CancellationToken;
        await using var wire = new ScriptedSession();
        await using var source = new MpgsqlDataSource(_ => ValueTask.FromResult(wire.Session),
            (_, _) => ValueTask.CompletedTask, new() { MaxConnections = 1, MaxInFlightPerConnection = 8 });
        var values = Enumerable.Range(0, requests).Select(i => source.ExecuteScalarAsync<long>(Sql,
            new[] { MpgsqlParameter.Int64(i) }, token).AsTask()).ToArray();
        int completed = 0;
        while (completed < requests)
        {
            byte[] bytes = await wire.ReadOutputAsync();
            var expected = new ArrayBufferWriter<byte>();
            var replies = new List<byte[]>();
            long value = -1;
            for (int offset = 0; offset < bytes.Length;)
            {
                int length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset + 1));
                char tag = (char)bytes[offset];
                if (tag == 'B')
                    // Two empty C strings, one binary format, one parameter and its int32 length.
                    value = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(offset + 17));
                else if (tag == 'S')
                {
                    Assert.InRange(value, 0, requests - 1);
                    ExpectedQuery(expected, value); FrontendMessage.Sync().Write(expected);
                    replies.Add(Query(value)); replies.Add(Ready()); completed++;
                }
                offset += length + 1;
            }
            Assert.Equal(expected.WrittenSpan.ToArray(), bytes);
            await wire.WriteAsync(Join([.. replies]), fragment: 7);
        }
        var results = await Task.WhenAll(values).WaitAsync(TestTimeout, token);
        for (int i = 0; i < requests; i++) Assert.Equal(i, results[i].Value);
        Assert.Equal(0, wire.Session.BufferedRowBytes);
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(257)]
    public async Task CombinedExecutionKeepsExactWireIndicesAndFollowingBoundary(int count)
    {
        await using var wire = new ScriptedSession(blockWrites: true);
        await using var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        var definitions = new QueryDefinition[count];
        var expected = new ArrayBufferWriter<byte>();
        for (int i = 0; i < count; i++)
        {
            definitions[i] = new(Sql, new[] { MpgsqlParameter.Int64(i) });
            ExpectedQuery(expected, i);
        }
        FrontendMessage.Sync().Write(expected);
        var work = batch.SendExecution(default, definitions);
        await using var next = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        var following = next.SendExecution(new(Sql, new[] { MpgsqlParameter.Int64(999) }), null);
        ExpectedQuery(expected, 999);
        FrontendMessage.Sync().Write(expected);
        var output = new List<byte>();
        while (Tags([.. output]).Count(tag => tag == 'S') != 2) output.AddRange(await wire.ReadOutputAsync());
        Assert.Equal(expected.WrittenSpan.ToArray(), output.ToArray());
        await Task.WhenAll(work.Completion, work.Delivery, following.Completion, following.Delivery)
            .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        var responses = new List<byte[]>();
        for (int i = 0; i < count; i++) responses.Add(Query(i));
        responses.Add(Ready()); responses.Add(Query(999)); responses.Add(Ready());
        await wire.WriteAsync(Join([.. responses]), fragment: 7);
        await using var reader = await batch.ReadResultsAsync();
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(i, reader.QueryIndex);
            Assert.True(await reader.ReadAsync()); Assert.Equal(i, reader.GetInt64(0));
            Assert.False(await reader.ReadAsync()); Assert.Equal(i + 1 < count, await reader.NextResultAsync());
        }
        if (count == 0) Assert.False(await reader.NextResultAsync());
        await batch.Completion;
        await using var nextReader = await next.ReadResultsAsync();
        Assert.True(await nextReader.ReadAsync()); Assert.Equal(999, nextReader.GetInt64(0));
        Assert.False(await nextReader.NextResultAsync());
        await next.Completion;
        Assert.True(wire.Session.IsIdleAndHealthy);
        Assert.Equal(0, wire.Session.BufferedRowBytes);
    }

    [Fact]
    public async Task SmallUncancellableExecutionSharesItsAcknowledgement()
    {
        await using var wire = new ScriptedSession();
        // An uncancellable request is required to verify the shared acknowledgement path.
#pragma warning disable xUnit1051
        await using var batch = wire.Session.CreateBatch();
#pragma warning restore xUnit1051
        var work = batch.SendExecution(new(Sql, new[] { MpgsqlParameter.Int64(11) }), null);
        Assert.Same(work.Completion, work.Delivery);
        var expected = new ArrayBufferWriter<byte>(); ExpectedQuery(expected, 11); FrontendMessage.Sync().Write(expected);
        Assert.Equal(expected.WrittenSpan.ToArray(), await wire.ReadOutputAsync());
        await work.Delivery;
        await wire.WriteAsync(Join(Query(11), Ready()));
        await using var reader = await batch.ReadResultsAsync();
        Assert.True(await reader.ReadAsync()); Assert.Equal(11, reader.GetInt64(0));
        Assert.False(await reader.NextResultAsync());
        await batch.Completion;
    }
}
