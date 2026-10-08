using Mpgsql.Protocol;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class ReceiveCompletionNotificationTests
{
    [Theory]
    [InlineData(0, int.MaxValue)]
    [InlineData(1, int.MaxValue)]
    [InlineData(2, int.MaxValue)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    public async Task CompletedGroupRowsWakeBeforeTheFollowingGroupWaitsForCapacity(int release, int fragment)
    {
        var token = TestContext.Current.CancellationToken;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
        await using var wire = new ScriptedSession();
        wire.Session.ClaimForDataSource(14);
        await using var first = wire.Session.CreateBatch(request.Token);
        await using var second = wire.Session.CreateBatch(token);
        await SendAsync(first, "select first");
        await SendAsync(second, "select second");
        Assert.Equal("PBDESPBDES", new string(Tags(await wire.ReadOutputAsync())));
        var firstOpening = first.ReadResultsAsync().AsTask();
        var secondOpening = second.ReadResultsAsync().AsTask();
        var writing = wire.WriteAsync(Join(Query(11), Ready(), Query(22), Ready()), fragment);
        await using var reader = await firstOpening.WaitAsync(TestTimeout, token);
        Assert.True(await reader.ReadAsync());
        Assert.Equal(11, reader.GetInt64(0));
        Assert.Equal(0, reader.QueryIndex);
        Assert.Equal(14, wire.Session.BufferedRowBytes);
        if (release == 2)
        {
            request.Cancel();
        }
        if (release != 0)
        {
            await reader.DisposeAsync().AsTask().WaitAsync(TestTimeout, token);
        }
        else
        {
            await FinishAsync(reader, token);
        }
        await using var next = await secondOpening.WaitAsync(TestTimeout, token);
        Assert.True(await next.ReadAsync());
        Assert.Equal(22, next.GetInt64(0));
        Assert.Equal(0, next.QueryIndex);
        await FinishAsync(next, token);
        await writing.WaitAsync(TestTimeout, token);
        await Task.WhenAll(first.Completion, second.Completion).WaitAsync(TestTimeout, token);
        await first.DisposeAsync();
        await second.DisposeAsync();
        await FollowingAsync(wire, token);
    }

    [Theory]
    [InlineData(false, int.MaxValue)]
    [InlineData(true, int.MaxValue)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    public async Task EmptyOrFailedCompletedGroupDoesNotHideTheFollowingResult(bool error, int fragment)
    {
        var token = TestContext.Current.CancellationToken;
        await using var wire = new ScriptedSession();
        wire.Session.ClaimForDataSource(14);
        await using var first = wire.Session.CreateBatch(token);
        await using var second = wire.Session.CreateBatch(token);
        await SendAsync(first, "first");
        await SendAsync(second, "second");
        await wire.ReadOutputAsync();
        var opening = first.ReadResultsAsync().AsTask();
        var nextOpening = second.ReadResultsAsync().AsTask();
        var initial = error ? Error() : Join(Packet('1'), Packet('2'), Packet('n'), Command("UPDATE 0"));
        var writing = wire.WriteAsync(Join(initial, Ready(), Query(77), Ready()), fragment);
        if (error)
        {
            var failure = await Assert.ThrowsAsync<MpgsqlServerException>(() => opening.WaitAsync(TestTimeout, token));
            Assert.Equal("22012", failure.SqlState);
            Assert.Equal(0, failure.QueryIndex);
            Assert.Equal(TransactionStatus.Idle, failure.TransactionStatus);
            await Assert.ThrowsAsync<MpgsqlServerException>(() => first.Completion.WaitAsync(TestTimeout, token));
        }
        else
        {
            await using var reader = await opening.WaitAsync(TestTimeout, token);
            Assert.False(reader.IsRowSet);
            Assert.False(await reader.ReadAsync());
            Assert.Equal("UPDATE 0", reader.CommandTag);
            Assert.False(await reader.NextResultAsync().AsTask().WaitAsync(TestTimeout, token));
            await first.Completion.WaitAsync(TestTimeout, token);
        }
        await using var next = await nextOpening.WaitAsync(TestTimeout, token);
        Assert.True(await next.ReadAsync());
        Assert.Equal(77, next.GetInt64(0));
        await FinishAsync(next, token);
        await writing.WaitAsync(TestTimeout, token);
        await second.Completion.WaitAsync(TestTimeout, token);
        await first.DisposeAsync();
        await second.DisposeAsync();
        await FollowingAsync(wire, token);
    }

    [Fact]
    public async Task ManyCompletedIndependentGroupsPreserveEveryValueAndReleaseAllRows()
    {
        var token = TestContext.Current.CancellationToken;
        await using var wire = new ScriptedSession();
        wire.Session.ClaimForDataSource(4096);
        const int count = 137;
        var batches = new MpgsqlQueryBatch[count];
        try
        {
            for (int i = 0; i < count; i++)
            {
                batches[i] = wire.Session.CreateBatch(token);
                await SendAsync(batches[i], "select identity");
            }
            Assert.Equal(string.Concat(Enumerable.Repeat("PBDES", count)), new string(Tags(await wire.ReadOutputAsync())));
            var openings = batches.Select(batch => batch.ReadResultsAsync().AsTask()).ToArray();
            await wire.WriteAsync(Join(Enumerable.Range(1, count).Select(i => Join(Query(i), Ready())).ToArray()));
            for (int i = 0; i < count; i++)
            {
                await using var reader = await openings[i].WaitAsync(TestTimeout, token);
                Assert.True(await reader.ReadAsync());
                Assert.Equal(i + 1, reader.GetInt64(0));
                Assert.Equal(0, reader.QueryIndex);
                await FinishAsync(reader, token);
                await batches[i].Completion.WaitAsync(TestTimeout, token);
                await batches[i].DisposeAsync();
            }
            await FollowingAsync(wire, token);
        }
        finally { foreach (var batch in batches) if (batch is not null)
            {
                await batch.DisposeAsync();
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TruncatedFollowingFrameDoesNotStrandACompletedGroup(bool payload)
    {
        var token = TestContext.Current.CancellationToken;
        await using var wire = new ScriptedSession();
        await using var first = wire.Session.CreateBatch(token);
        await using var second = wire.Session.CreateBatch(token);
        await SendAsync(first, "first");
        await SendAsync(second, "second");
        await wire.ReadOutputAsync();
        var opening = first.ReadResultsAsync().AsTask();
        var nextOpening = second.ReadResultsAsync().AsTask();
        var truncated = payload ? Join(Begin(20), Row(Int64(22))[..9]) : Packet('1')[..1];
        var writing = wire.WriteAsync(Join(Query(11), Ready(), truncated));
        await writing.WaitAsync(TestTimeout, token);
        await wire.Incoming.Writer.CompleteAsync();
        await using var reader = await opening.WaitAsync(TestTimeout, token);
        Assert.True(await reader.ReadAsync());
        Assert.Equal(11, reader.GetInt64(0));
        await FinishAsync(reader, token);
        await first.Completion.WaitAsync(TestTimeout, token);
        var followingError = await Record.ExceptionAsync(async () =>
        {
            await using var next = await nextOpening.WaitAsync(TestTimeout, token);
            await next.ReadAsync().AsTask().WaitAsync(TestTimeout, token);
        });
        Assert.IsType<EndOfStreamException>(followingError);
        await Assert.ThrowsAsync<EndOfStreamException>(() => wire.Session.Completion.WaitAsync(TestTimeout, token));
        Assert.False(wire.Session.IsHealthy);
    }

    private static async Task SendAsync(MpgsqlQueryBatch batch, string sql)
    {
        await batch.SendQueryAsync(sql);
        await batch.SendSyncAsync();
    }

    private static async Task FinishAsync(MpgsqlResultReader reader, CancellationToken token)
    {
        Assert.False(await reader.ReadAsync().AsTask().WaitAsync(TestTimeout, token));
        Assert.False(await reader.NextResultAsync().AsTask().WaitAsync(TestTimeout, token));
        await reader.DisposeAsync().AsTask().WaitAsync(TestTimeout, token);
    }

    private static async Task FollowingAsync(ScriptedSession wire, CancellationToken token)
    {
        Assert.Equal(0, wire.Session.BufferedRowBytes);
        Assert.True(wire.Session.IsIdleAndHealthy);
        await using var batch = wire.Session.CreateBatch(token);
        await SendAsync(batch, "following");
        Assert.Equal("PBDES", new string(Tags(await wire.ReadOutputAsync())));
        await wire.WriteAsync(Join(Query(777), Ready()));
        await using var reader = await batch.ReadResultsAsync().AsTask().WaitAsync(TestTimeout, token);
        Assert.True(await reader.ReadAsync());
        Assert.Equal(777, reader.GetInt64(0));
        await FinishAsync(reader, token);
        await batch.Completion.WaitAsync(TestTimeout, token);
        Assert.Equal(0, wire.Session.BufferedRowBytes);
        Assert.True(wire.Session.IsIdleAndHealthy);
    }
}
