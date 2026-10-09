using System.Threading.Channels;
using Mpgsql.Internal;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class RowBufferBudgetContractTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OneProducerAndConcurrentReleasesKeepEveryDiagnosticSnapshotBounded()
    {
        await using var wire = new ScriptedSession();
        await using var batch = wire.Session.CreateBatch(Token);
        var budget = new RowBufferBudget(64);
        var channel = Channel.CreateBounded<int>(new BoundedChannelOptions(64)
        {
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
        var released = 0L;
        var stop = 0;
        var observing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = Task.Run(async () =>
        {
            observing.TrySetResult();
            while (Volatile.Read(ref stop) == 0)
            {
                Assert.InRange(budget.Used, 0L, 64L);
                await Task.Yield();
            }
        }, Token);
        var consumers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await foreach (var bytes in channel.Reader.ReadAllAsync(Token))
            {
                budget.Release(bytes);
                Interlocked.Add(ref released, bytes);
            }
        }, Token)).ToArray();
        var producer = Task.Run(async () =>
        {
            var total = 0L;
            try
            {
                await observing.Task;
                for (var i = 0; i < 2048; i++)
                {
                    var bytes = (i % 5) switch { 0 => 1, 1 => 2, 2 => 3, 3 => 7, _ => 16 };
                    Assert.True(await budget.ReserveAsync(bytes, batch, Token));
                    try { await channel.Writer.WriteAsync(bytes, Token); }
                    catch
                    {
                        budget.Release(bytes);
                        throw;
                    }
                    total += bytes;
                }
                return total;
            }
            finally { channel.Writer.TryComplete(); }
        }, Token);
        try { await Task.WhenAll(consumers.Append(producer)).WaitAsync(TestTimeout, Token); }
        finally
        {
            Volatile.Write(ref stop, 1);
            await observer.WaitAsync(TestTimeout, Token);
        }
        Assert.Equal(await producer, Volatile.Read(ref released));
        Assert.Equal(0, budget.Used);
        Assert.False(budget.WouldBlock(128));
        Assert.True(await budget.ReserveAsync(128, batch, Token));
        Assert.Equal(128, budget.Used);
        Assert.True(budget.WouldBlock(1));
        budget.Release(128);
        Assert.Equal(0, budget.Used);
    }

    [Fact]
    public async Task PartialReleaseWakeupCancellationAndOversizedRowsPreserveProgress()
    {
        await using var wire = new ScriptedSession();
        await using var batch = wire.Session.CreateBatch(Token);
        var budget = new RowBufferBudget(14);
        Assert.True(await budget.ReserveAsync(14, batch, Token));
        Assert.Equal(14, budget.Used);
        var waiting = budget.ReserveAsync(14, batch, Token).AsTask();
        Assert.False(waiting.IsCompleted);
        budget.Release(6);
        Assert.Equal(8, budget.Used);
        Assert.True(budget.WouldBlock(14));
        budget.Release(8);
        Assert.True(await waiting.WaitAsync(TestTimeout, Token));
        Assert.Equal(14, budget.Used);

        using var cancelled = new CancellationTokenSource();
        var oversized = budget.ReserveAsync(128, batch, cancelled.Token).AsTask();
        Assert.False(oversized.IsCompleted);
        cancelled.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oversized.WaitAsync(TestTimeout, Token));
        Assert.Equal(cancelled.Token, error.CancellationToken);
        Assert.Equal(14, budget.Used);
        budget.Release(14);
        Assert.Equal(0, budget.Used);
        Assert.True(await budget.ReserveAsync(128, batch, Token));
        Assert.Equal(128, budget.Used);
        var following = budget.ReserveAsync(1, batch, Token).AsTask();
        Assert.False(following.IsCompleted);
        budget.Release(100);
        Assert.Equal(28, budget.Used);
        Assert.False(following.IsCompleted);
        budget.Release(28);
        Assert.True(await following.WaitAsync(TestTimeout, Token));
        Assert.Equal(1, budget.Used);
        budget.Release(1);
        Assert.Equal(0, budget.Used);
    }

    [Fact]
    public async Task DiscardAndCancellationRaceDoNotAdmitAnOldWaitOrLeakIntoTheFollowingWait()
    {
        await using var wire = new ScriptedSession();
        await using var discardedBatch = wire.Session.CreateBatch(Token);
        var budget = new RowBufferBudget(14);
        Assert.True(await budget.ReserveAsync(14, discardedBatch, Token));
        var discarded = budget.ReserveAsync(1, discardedBatch, Token).AsTask();
        Assert.False(discarded.IsCompleted);
        discardedBatch.BeginDiscard();
        budget.Pulse(); // This standalone budget is not the scripted session's own budget.
        Assert.False(await discarded.WaitAsync(TestTimeout, Token));
        Assert.Equal(14, budget.Used);
        budget.Release(14);
        Assert.Equal(0, budget.Used);

        await using var batch = wire.Session.CreateBatch(Token);
        Assert.True(await budget.ReserveAsync(14, batch, Token));
        using var cancelled = new CancellationTokenSource();
        var racing = budget.ReserveAsync(14, batch, cancelled.Token).AsTask();
        Assert.False(racing.IsCompleted);
        await Task.WhenAll(Task.Run(cancelled.Cancel, Token), Task.Run(() => budget.Release(14), Token));
        try
        {
            Assert.True(await racing.WaitAsync(TestTimeout, Token));
            budget.Release(14);
        }
        catch (OperationCanceledException) { }
        Assert.Equal(0, budget.Used);
        Assert.True(await budget.ReserveAsync(14, batch, Token));
        var following = budget.ReserveAsync(1, batch, Token).AsTask();
        Assert.False(following.IsCompleted);
        budget.Release(14);
        Assert.True(await following.WaitAsync(TestTimeout, Token));
        budget.Release(1);
        Assert.Equal(0, budget.Used);
    }

}
