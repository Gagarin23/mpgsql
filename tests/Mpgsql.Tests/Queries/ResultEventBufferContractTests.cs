using System.Buffers;
using Mpgsql.Converters;
using Mpgsql.Internal;
using Mpgsql.Protocol;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class ResultEventBufferContractTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(7, false)]
    [InlineData(7, true)]
    [InlineData(19, false)]
    [InlineData(19, true)]
    [InlineData(73, false)]
    [InlineData(73, true)]
    public async Task BufferedEventsPreserveFifoAndExposeCompletionOnlyAfterConsumption(int count, bool fault)
    {
        var buffer = new ResultEventBuffer();
        var expected = Enumerable.Range(0, count)
            .Select(i => new ResultEvent(i, default, CommandTag: "tag-" + i, IsEnd: i % 3 == 0, IsRowSet: i % 3 == 1))
            .ToArray();
        foreach (var item in expected)
        {
            Assert.True(buffer.TryWrite(item, false));
        }
        var error = fault ? new IOException("after buffered events") : null;
        buffer.Complete(error);
        foreach (var item in expected)
        {
            Assert.True(await buffer.WaitToReadAsync());
            Assert.True(buffer.TryRead(out var actual));
            Assert.Equal(item, actual);
        }
        Assert.False(buffer.TryRead(out _));
        if (fault)
        {
            Assert.Same(error, await Assert.ThrowsAsync<IOException>(async () => await buffer.WaitToReadAsync()));
        }
        else
        {
            Assert.False(await buffer.WaitToReadAsync());
        }
    }

    [Fact]
    public async Task DeferredNotificationReleasesAnExistingWaiterWhenUnreadEventsRemain()
    {
        var buffer = new ResultEventBuffer();
        var waiting = buffer.WaitToReadAsync();
        for (var i = 0; i < 10; i++)
        {
            Assert.True(buffer.TryWrite(new ResultEvent(i, default), false));
        }
        Assert.True(buffer.TryRead(out var first));
        Assert.Equal(0, first.QueryIndex);
        Assert.False(waiting.IsCompleted);
        buffer.NotifyAvailable();
        Assert.True(await waiting);
        for (var i = 1; i < 10; i++)
        {
            Assert.True(buffer.TryRead(out var item));
            Assert.Equal(i, item.QueryIndex);
        }
        buffer.Complete();
        Assert.False(await buffer.WaitToReadAsync());
    }

    [Fact]
    public async Task DrainReleasesUnreadRowsAndLeavesTheTransferredRowOwnedByItsConsumer()
    {
        await using var wire = new ScriptedSession();
        await using var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        var budget = new RowBufferBudget(1 << 20);
        var buffer = new ResultEventBuffer();
        var owners = await WriteRowsAsync(buffer, budget, batch, 47);
        Assert.True(buffer.TryRead(out var claimed));
        buffer.Drain();
        buffer.Complete();
        Assert.Equal(14, budget.Used);
        Assert.Equal(0, owners[0].Disposals);
        Assert.All(owners.Skip(1), owner => Assert.Equal(1, owner.Disposals));
        Assert.Equal(0, Int64Converter.Read(claimed.Row!.Value[0]!.Value));
        claimed.Row.Value.Dispose();
        Assert.Equal(0, budget.Used);
        Assert.All(owners, owner => Assert.Equal(1, owner.Disposals));
        Assert.False(await buffer.WaitToReadAsync());
    }

    [Fact]
    public async Task ConcurrentDrainDoesNotReleaseTransferredRows()
    {
        var token = TestContext.Current.CancellationToken;
        await using var wire = new ScriptedSession();
        await using var batch = wire.Session.CreateBatch(token);
        for (var repeat = 0; repeat < 16; repeat++)
        {
            var budget = new RowBufferBudget(1 << 20);
            var buffer = new ResultEventBuffer();
            var owners = await WriteRowsAsync(buffer, budget, batch, 137);
            Assert.True(buffer.TryRead(out var first));
            first.Row!.Value.Dispose();
            buffer.Complete();
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reading = Task.Run(async () =>
            {
                await start.Task;
                while (buffer.TryRead(out var item))
                {
                    Assert.Equal(item.QueryIndex, Int64Converter.Read(item.Row!.Value[0]!.Value));
                    item.Row.Value.Dispose();
                }
            }, token);
            var draining = Task.Run(async () =>
            {
                await start.Task;
                buffer.Drain();
            }, token);
            start.SetResult();
            await Task.WhenAll(reading, draining).WaitAsync(TestTimeout, token);
            buffer.Drain();
            Assert.Equal(0, budget.Used);
            Assert.All(owners, owner => Assert.Equal(1, owner.Disposals));
            Assert.False(await buffer.WaitToReadAsync());
        }
    }

    [Fact]
    public async Task ADrainedBufferCanBeReusedWithoutExposingPreviousEvents()
    {
        var buffer = new ResultEventBuffer();
        for (var i = 0; i < 47; i++)
        {
            Assert.True(buffer.TryWrite(new ResultEvent(i, default)));
        }
        Assert.True(buffer.TryRead(out var first));
        Assert.Equal(0, first.QueryIndex);
        buffer.Drain();
        for (var i = 100; i < 141; i++)
        {
            Assert.True(buffer.TryWrite(new ResultEvent(i, default)));
        }
        buffer.Complete();
        for (var i = 100; i < 141; i++)
        {
            Assert.True(buffer.TryRead(out var item));
            Assert.Equal(i, item.QueryIndex);
        }
        Assert.False(await buffer.WaitToReadAsync());
    }

    [Fact]
    public async Task CancellationDrainsBufferedRowsWhileTheReaderDoesNotMove()
    {
        var token = TestContext.Current.CancellationToken;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
        await using var wire = new ScriptedSession();
        wire.Session.ClaimForDataSource(1024);
        await using var batch = wire.Session.CreateBatch(request.Token);
        await batch.SendQueryAsync("select buffered rows");
        await batch.SendSyncAsync();
        await wire.ReadOutputAsync();
        var frames = new List<byte[]> { Begin(20) };
        frames.AddRange(Enumerable.Range(0, 40).Select(i => Row(Int64(i))));
        await wire.WriteAsync(Join(frames.ToArray()));
        await using var reader = await batch.ReadResultsAsync();
        Assert.Equal(40 * 14, wire.Session.BufferedRowBytes);
        request.Cancel();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TestTimeout);
        while (wire.Session.BufferedRowBytes != 0)
        {
            await Task.Delay(1, deadline.Token);
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reader.ReadAsync());
        await wire.WriteAsync(Join(Command("SELECT 40"), Ready()));
        await batch.Completion.WaitAsync(TestTimeout, token);
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    private static async Task<TrackingOwner[]> WriteRowsAsync(
        ResultEventBuffer buffer, RowBufferBudget budget, MpgsqlQueryBatch batch, int count)
    {
        var owners = new TrackingOwner[count];
        for (var i = 0; i < count; i++)
        {
            var bytes = Row(Int64(i)).AsMemory(5).ToArray();
            var owner = owners[i] = new TrackingOwner(bytes);
            var payload = new ReadOnlySequence<byte>(owner.Memory);
            Assert.True(await budget.ReserveAsync(payload.Length, batch, TestContext.Current.CancellationToken));
            var row = new OwnedRow(new BackendMessage((byte)'D', BackendMessageKind.DataRow, payload, 1), owner, budget);
            Assert.True(buffer.TryWrite(new ResultEvent(i, default, row)));
        }
        return owners;
    }

    private sealed class TrackingOwner(byte[] bytes) : IMemoryOwner<byte>
    {
        private int _disposals;
        public Memory<byte> Memory => bytes;
        internal int Disposals => Volatile.Read(ref _disposals);
        public void Dispose() => Interlocked.Increment(ref _disposals);
    }
}
