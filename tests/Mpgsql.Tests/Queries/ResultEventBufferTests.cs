using System.Buffers;
using Mpgsql.Internal;
using Mpgsql.Protocol;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class ResultEventBufferTests
{
    [ThreadStatic]
    private static bool _publishing;

    [Fact]
    public async Task WrapGrowAndCompletionPreserveFifo()
    {
        var buffer = new ResultEventBuffer();
        for (var i = 0;
             i < 12;
             i++)
        {
            Assert.True(buffer.TryWrite(new ResultEvent(i, default)));
        }
        for (var i = 0;
             i < 10;
             i++)
        {
            Assert.True(buffer.TryRead(out var item));
            Assert.Equal(i, item.QueryIndex);
        }
        for (var i = 12;
             i < 200;
             i++)
        {
            Assert.True(buffer.TryWrite(new ResultEvent(i, default)));
        }
        buffer.Complete();
        Assert.False(buffer.TryWrite(new ResultEvent(200, default)));
        for (var i = 10;
             i < 200;
             i++)
        {
            Assert.True(await buffer.WaitToReadAsync());
            Assert.True(buffer.TryRead(out var item));
            Assert.Equal(i, item.QueryIndex);
        }
        Assert.False(buffer.TryRead(out _));
        Assert.False(await buffer.WaitToReadAsync());
    }

    [Fact]
    public async Task AwaitedNotificationsCanBeReusedAndNeverInvokeReaderInline()
    {
        var buffer = new ResultEventBuffer();
        for (var i = 0;
             i < 100;
             i++)
        {
            var waiting = buffer.WaitToReadAsync();
            Assert.False(waiting.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => buffer.WaitToReadAsync());
            var resumed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var awaiter = waiting.GetAwaiter();
            awaiter.UnsafeOnCompleted
            (() =>
                {
                    try { resumed.SetResult(_publishing); }
                    catch (Exception error) { resumed.SetException(error); }
                }
            );
            _publishing = true;
            try { Assert.True(buffer.TryWrite(new ResultEvent(i, default))); }
            finally { _publishing = false; }
            Assert.False(await resumed.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
            Assert.True(await waiting);
            Assert.True(buffer.TryRead(out var item));
            Assert.Equal(i, item.QueryIndex);
        }
        var final = buffer.WaitToReadAsync();
        buffer.Complete();
        Assert.False(await final);
    }

    [Fact]
    public async Task FaultWakesWaiterAndCannotBeOverwrittenByNormalCompletion()
    {
        var buffer = new ResultEventBuffer();
        var waiting = buffer.WaitToReadAsync();
        var error = new IOException("transport failure");
        buffer.Complete(error);
        buffer.Complete();
        Assert.Same(error, await Assert.ThrowsAsync<IOException>(async () => await waiting));
        Assert.Same(error, await Assert.ThrowsAsync<IOException>(async () => await buffer.WaitToReadAsync()));
    }

    [Fact]
    public async Task DeferredNotificationPublishesTheWholeBurstWithoutHidingReadableItems()
    {
        var buffer = new ResultEventBuffer();
        var waiting = buffer.WaitToReadAsync();
        for (var i = 0;
             i < 20;
             i++)
        {
            Assert.True(buffer.TryWrite(new ResultEvent(i, default), false));
        }
        Assert.False(waiting.IsCompleted);
        Assert.True(buffer.TryRead(out var first));
        Assert.Equal(0, first.QueryIndex);
        buffer.NotifyAvailable();
        Assert.True(await waiting);
        for (var i = 1;
             i < 20;
             i++)
        {
            Assert.True(buffer.TryRead(out var item));
            Assert.Equal(i, item.QueryIndex);
        }
        var next = buffer.WaitToReadAsync();
        buffer.NotifyAvailable();
        Assert.False(next.IsCompleted);
        buffer.Complete();
        Assert.False(await next);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task CompletionExposesDeferredItemsBeforeItsTerminalOutcome(bool fault)
    {
        var buffer = new ResultEventBuffer();
        var waiting = buffer.WaitToReadAsync();
        Assert.True(buffer.TryWrite(new ResultEvent(7, default), false));
        var error = fault ? new IOException("after queued event") : null;
        buffer.Complete(error);
        Assert.True(await waiting);
        Assert.True(buffer.TryRead(out var item));
        Assert.Equal(7, item.QueryIndex);
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
    public async Task ConcurrentDrainAndReaderReleaseEveryRowReservationOnce()
    {
        await using var wire = new ScriptedSession();
        await using var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        var budget = new RowBufferBudget(1 << 20);
        var buffer = new ResultEventBuffer();
        for (var i = 0;
             i < 512;
             i++)
        {
            var payload = new ReadOnlySequence<byte>
            (
                Row(Int64(i))
                    .AsMemory(5)
            );
            Assert.True(await budget.ReserveAsync(payload.Length, batch, TestContext.Current.CancellationToken));
            var row = new OwnedRow(new BackendMessage((byte)'D', BackendMessageKind.DataRow, payload, 1), null, budget);
            Assert.True(buffer.TryWrite(new ResultEvent(i, default, row)));
        }
        buffer.Complete();
        await Task
            .WhenAll
            (
                Task.Run(buffer.Drain, TestContext.Current.CancellationToken), Task.Run
                (
                    () =>
                    {
                        while (buffer.TryRead(out var item))
                        {
                            item.Row!.Value.Dispose();
                        }
                    }, TestContext.Current.CancellationToken
                )
            )
            .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        buffer.Drain();
        Assert.Equal(0, budget.Used);
        Assert.False(await buffer.WaitToReadAsync());
    }
}