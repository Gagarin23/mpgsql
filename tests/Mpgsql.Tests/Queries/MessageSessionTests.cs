using System.Buffers.Binary;
using System.Text;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class MessageSessionTests
{
    [Fact]
    public async Task ReadingBeforeSyncAndDisposalNeverWriteSync()
    {
        await using var wire = new ScriptedSession();
        var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        var opening = batch.ReadResultsAsync().AsTask();
        Assert.False(wire.HasOutput());
        await batch.SendQueryAsync("select $1",
            new[]
            {
                MpgsqlParameterValue.Int64(42)
            });
        Assert.Equal(new[]
            {
                'P',
                'B',
                'D',
                'E'
            },
            Tags(await wire.ReadOutputAsync()));
        await wire.WriteAsync(Query(42),
            1);
        var reader = await opening.WaitAsync(TestTimeout,
            TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync());
        Assert.Equal(42,
            reader.GetInt64(0));
        Assert.False(await reader.ReadAsync());
        await reader.DisposeAsync();
        await batch.DisposeAsync();
        Assert.False(wire.HasOutput());
        Assert.False(batch.Completion.IsCompleted);
        await batch.SendSyncAsync(); // still permitted after consumption disposal
        Assert.Equal(new[]
            {
                'S'
            },
            Tags(await wire.ReadOutputAsync()));
        await wire.WriteAsync(Ready());
        await batch.Completion.WaitAsync(TestTimeout,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task UnawaitedSendsRemainOrderedBeforeExplicitSync()
    {
        await using var wire = new ScriptedSession();
        var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        var reading = batch.ReadResultsAsync().AsTask();
        Task[] sends =
        [
            .. Enumerable.Range(0,
                100).Select(i => batch.SendQueryAsync("select $1",
                new[] {MpgsqlParameterValue.Int64(i)}).AsTask())
        ];
        await batch.SendSyncAsync();
        await Task.WhenAll(sends).WaitAsync(TestTimeout,
            TestContext.Current.CancellationToken);
        var bytes = await wire.ReadOutputAsync();
        Assert.Equal(string.Concat(Enumerable.Repeat("PBDE",
                         100))
                     + "S",
            new string(Tags(bytes)));
        // Each bigint payload remains in invocation order, even though no send was immediately awaited.
        var offset = 0;
        for (var i = 0; i < 100; i++, offset += 68)
        {
            Assert.Equal(i,
                BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(offset + 39)));
        }
        await wire.WriteAsync(Join([
            .. Enumerable.Range(0,
                100).Select(i => Query(i)),
            Ready()
        ]));
        await using var reader = await reading.WaitAsync(TestTimeout,
            TestContext.Current.CancellationToken);
        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(i,
                reader.QueryIndex);
            Assert.True(await reader.ReadAsync());
            Assert.Equal(i,
                reader.GetInt64(0));
            Assert.False(await reader.ReadAsync());
            Assert.Equal("SELECT 1",
                reader.CommandTag);
            Assert.Equal(i < 99,
                await reader.NextResultAsync());
        }
        await batch.DisposeAsync();
    }

    [Fact]
    public async Task NextResultCanWaitForMoreSendsInAnOpenGroup()
    {
        await using var wire = new ScriptedSession();
        var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await batch.SendQueryAsync("select 1::bigint");
        await wire.WriteAsync(Query(1));
        var reader = await batch.ReadResultsAsync();
        Assert.True(await reader.ReadAsync());
        Assert.False(await reader.ReadAsync());
        var next = reader.NextResultAsync().AsTask();
        Assert.False(next.IsCompleted);
        await batch.SendQueryAsync("update something");
        await wire.WriteAsync(Join(Packet('1'),
            Packet('2'),
            Packet('n'),
            Command("UPDATE 7")));
        Assert.True(await next.WaitAsync(TestTimeout,
            TestContext.Current.CancellationToken));
        Assert.Empty(reader.Columns.ToArray());
        Assert.False(await reader.ReadAsync());
        Assert.Equal("UPDATE 7",
            reader.CommandTag);
        var end = reader.NextResultAsync().AsTask();
        Assert.False(end.IsCompleted);
        await batch.SendSyncAsync();
        await wire.WriteAsync(Ready());
        Assert.False(await end.WaitAsync(TestTimeout,
            TestContext.Current.CancellationToken));
        await reader.DisposeAsync();
        await batch.DisposeAsync();
    }

    [Fact]
    public async Task SeveralGroupsCanBeConsumedInReverseOrder()
    {
        await using var wire = new ScriptedSession();
        var a = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        var b = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await a.SendQueryAsync("select 1::bigint");
        Assert.Throws<InvalidOperationException>(() => b.SendQueryAsync("select 2::bigint"));
        await a.SendSyncAsync();
        await b.SendQueryAsync("select 2::bigint");
        await b.SendSyncAsync();
        await wire.WriteAsync(Join(Query(1),
            Ready(),
            Query(2),
            Ready()));
        await using var rb = await b.ReadResultsAsync();
        Assert.True(await rb.ReadAsync());
        Assert.Equal(2,
            rb.GetInt64(0));
        Assert.False(await rb.NextResultAsync());
        await using var ra = await a.ReadResultsAsync();
        Assert.True(await ra.ReadAsync());
        Assert.Equal(1,
            ra.GetInt64(0));
        Assert.False(await ra.NextResultAsync());
        await a.DisposeAsync();
        await b.DisposeAsync();
    }

    [Theory, InlineData(0), InlineData(1), InlineData(2), InlineData(3), InlineData(4)]
    public async Task ErrorsAtEveryStageSurfaceOnlyAtReady(int stage)
    {
        await using var wire = new ScriptedSession();
        var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await batch.SendQueryAsync("bad query");
        await batch.SendQueryAsync("skipped query");
        await batch.SendSyncAsync();
        var prefixes = new[]
        {
            [], Packet('1'), Join(Packet('1'),
                Packet('2')),
            Join(Packet('1'),
                Packet('2'),
                Packet('n')),
            Join(Packet('1'),
                Packet('2'),
                Packet('n'),
                Command("UPDATE 1"),
                Packet('1'),
                Packet('2'),
                Packet('n'),
                Command("UPDATE 1"))
        };
        var readerTask = batch.ReadResultsAsync().AsTask();
        MpgsqlResultReader? reader = null;
        if (stage >= 3)
        {
            await wire.WriteAsync(prefixes[stage]);
            reader = await readerTask.WaitAsync(TestTimeout,
                TestContext.Current.CancellationToken);
            if (stage == 4)
            {
                Assert.False(await reader.ReadAsync());
                Assert.True(await reader.NextResultAsync());
                Assert.False(await reader.ReadAsync());
            }
        }
        else
        {
            await wire.WriteAsync(prefixes[stage]);
        }
        await wire.WriteAsync(Error());
        Task waiting = reader is null ? readerTask : reader.NextResultAsync().AsTask();
        Assert.False(waiting.IsCompleted);
        await wire.WriteAsync(Ready());
        var error = await Assert.ThrowsAsync<MpgsqlServerException>(() => waiting.WaitAsync(TestTimeout,
            TestContext.Current.CancellationToken));
        Assert.Equal(stage == 4
                ? null
                : 0,
            error.QueryIndex);
        Assert.Equal("22012",
            error.SqlState);
        if (reader is not null)
        {
            Assert.False(await reader.ReadAsync());
            Assert.False(await reader.NextResultAsync());
            await reader.DisposeAsync();
        }
        await batch.DisposeAsync();
        var recovery = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await recovery.SendQueryAsync("select 9::bigint");
        await recovery.SendSyncAsync();
        await wire.WriteAsync(Join(Query(9),
            Ready()));
        await using var recovered = await recovery.ReadResultsAsync();
        Assert.True(await recovered.ReadAsync());
        Assert.Equal(9,
            recovered.GetInt64(0));
        Assert.False(await recovered.NextResultAsync());
        await recovery.DisposeAsync();
    }

    [Fact]
    public async Task InterleavedEventsDoNotChangeQueryState()
    {
        await using var wire = new ScriptedSession();
        var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await batch.SendQueryAsync("select 1::bigint");
        await batch.SendSyncAsync();
        await wire.WriteAsync(Join(Packet('1'),
            Packet('N',
                Encoding.UTF8.GetBytes("Mhello\0\0")),
            Packet('2'),
            Packet('S',
                Encoding.UTF8.GetBytes("x\0y\0")),
            Description(20),
            Row(Int64(1)),
            Command(),
            Packet('A',
            [
                0,
                0,
                0,
                42,
                .. Encoding.UTF8.GetBytes("channel\0payload\0")
            ]),
            Ready()));
        await batch.Completion.WaitAsync(TestTimeout,
            TestContext.Current.CancellationToken);
        Assert.True(wire.Session.TryReadNotice(out var notice));
        Assert.Equal("hello",
            notice.Message);
        Assert.True(wire.Session.TryReadNotification(out var notification));
        Assert.Equal("payload",
            notification.Payload);
        Assert.True(wire.Session.TryGetParameter("x",
            out var value));
        Assert.Equal("y",
            value);
        await using var reader = await batch.ReadResultsAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1,
            reader.GetInt64(0));
        Assert.False(await reader.NextResultAsync());
        await batch.DisposeAsync();
    }

    [Fact]
    public async Task DuplicateSyncAndReadsCannotAddExtraBoundaries()
    {
        await using var wire = new ScriptedSession();
        var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await batch.SendSyncAsync();
        Assert.Throws<InvalidOperationException>(() => batch.SendSyncAsync());
        Assert.Throws<InvalidOperationException>(() => batch.SendQueryAsync("select 1"));
        await wire.WriteAsync(Ready());
        await using var reader = await batch.ReadResultsAsync();
        Assert.False(await reader.ReadAsync());
        Assert.False(await reader.NextResultAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => batch.ReadResultsAsync().AsTask());
        Assert.Equal(new[]
            {
                'S'
            },
            Tags(await wire.ReadOutputAsync()));
        await batch.DisposeAsync();
    }
}