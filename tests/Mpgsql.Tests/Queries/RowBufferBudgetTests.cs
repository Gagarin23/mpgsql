using Mpgsql.Internal;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class RowBufferBudgetTests
{
    [ThreadStatic] private static bool _publishing;

    [Fact]
    public async Task CancelledCapacityWaitCanBeFollowedByANewReservation()
    {
        await using var wire = new ScriptedSession();
        await using var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        var budget = new RowBufferBudget(14);
        for (int i = 0; i < 128; i++)
        {
            Assert.True(await budget.ReserveAsync(14, batch, TestContext.Current.CancellationToken));
            using var cancelled = new CancellationTokenSource();
            var pending = budget.ReserveAsync(14, batch, cancelled.Token).AsTask();
            Assert.False(pending.IsCompleted);
            cancelled.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TestTimeout,
                TestContext.Current.CancellationToken));
            Assert.Equal(cancelled.Token, error.CancellationToken);
            Assert.Equal(14, budget.Used);
            budget.Release(14);
            Assert.True(await budget.ReserveAsync(14, batch, TestContext.Current.CancellationToken));
            budget.Release(14);
            Assert.Equal(0, budget.Used);
        }
    }

    [Fact]
    public async Task CancellationRacingReleaseDoesNotCompleteTheNextWaitOrLeakCapacity()
    {
        await using var wire = new ScriptedSession();
        await using var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        var budget = new RowBufferBudget(14);
        for (int i = 0; i < 128; i++)
        {
            Assert.True(await budget.ReserveAsync(14, batch, TestContext.Current.CancellationToken));
            using var cancelled = new CancellationTokenSource();
            var pending = budget.ReserveAsync(14, batch, cancelled.Token).AsTask();
            await Task.WhenAll(Task.Run(cancelled.Cancel, TestContext.Current.CancellationToken),
                Task.Run(() => budget.Release(14), TestContext.Current.CancellationToken));
            try
            {
                Assert.True(await pending.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
                budget.Release(14);
            }
            catch (OperationCanceledException) { }
            Assert.Equal(0, budget.Used);
            Assert.True(await budget.ReserveAsync(14, batch, TestContext.Current.CancellationToken));
            var following = budget.ReserveAsync(14, batch, TestContext.Current.CancellationToken).AsTask();
            Assert.False(following.IsCompleted);
            budget.Release(14);
            Assert.True(await following.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
            budget.Release(14);
        }
    }

    [Fact]
    public async Task ReleaseNeverContinuesTheReceiverInline()
    {
        await using var wire = new ScriptedSession();
        await using var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        var budget = new RowBufferBudget(14);
        Assert.True(await budget.ReserveAsync(14, batch, TestContext.Current.CancellationToken));
        var pending = budget.ReserveAsync(14, batch, TestContext.Current.CancellationToken);
        var resumed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending.GetAwaiter().UnsafeOnCompleted(() => resumed.SetResult(_publishing));
        _publishing = true;
        try { budget.Release(14); }
        finally { _publishing = false; }
        Assert.False(await resumed.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
        Assert.True(await pending);
        budget.Release(14);
        Assert.Equal(0, budget.Used);
    }

    [Fact]
    public async Task ReleaseRacingReservationRegistrationNeverLosesAWakeup()
    {
        await using var wire = new ScriptedSession();
        await using var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        var budget = new RowBufferBudget(14);
        for (int round = 0; round < 1000; round++)
        {
            Assert.True(await budget.ReserveAsync(14, batch, TestContext.Current.CancellationToken));
            var release = Task.Run(() => budget.Release(14), TestContext.Current.CancellationToken);
            Assert.True(await budget.ReserveAsync(14, batch, TestContext.Current.CancellationToken)
                .AsTask().WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
            await release;
            Assert.Equal(14, budget.Used);
            budget.Release(14);
            Assert.Equal(0, budget.Used);
        }
    }

    [Fact]
    public async Task ConcurrentReleasesAllowOneOversizedRowOnlyWhenEmpty()
    {
        await using var wire = new ScriptedSession();
        await using var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        var budget = new RowBufferBudget(64);
        for (int round = 0; round < 64; round++)
        {
            for (int i = 0; i < 64; i++)
                Assert.True(await budget.ReserveAsync(1, batch, TestContext.Current.CancellationToken));
            var oversized = budget.ReserveAsync(128, batch, TestContext.Current.CancellationToken).AsTask();
            Assert.False(oversized.IsCompleted);
            await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() => budget.Release(1),
                TestContext.Current.CancellationToken)));
            Assert.True(await oversized.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
            Assert.Equal(128, budget.Used);
            var next = budget.ReserveAsync(1, batch, TestContext.Current.CancellationToken).AsTask();
            Assert.False(next.IsCompleted);
            budget.Release(128);
            Assert.True(await next.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
            budget.Release(1);
            Assert.Equal(0, budget.Used);
        }
    }

    [Fact]
    public async Task DiscardWakesReservationWithoutAdmittingOrLeakingBytes()
    {
        await using var wire = new ScriptedSession();
        await using var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        var budget = new RowBufferBudget(14);
        Assert.True(await budget.ReserveAsync(14, batch, TestContext.Current.CancellationToken));
        var next = budget.ReserveAsync(14, batch, TestContext.Current.CancellationToken).AsTask();
        Assert.False(next.IsCompleted);
        batch.BeginDiscard();
        budget.Pulse(); // this standalone budget is not attached to the scripted session
        Assert.False(await next.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
        Assert.Equal(14, budget.Used);
        budget.Release(14);
        Assert.Equal(0, budget.Used);
    }
}
