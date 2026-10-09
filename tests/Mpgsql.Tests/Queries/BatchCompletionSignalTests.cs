using Mpgsql.Internal;

namespace Mpgsql.Tests.Queries;

public sealed class BatchCompletionSignalTests
{
    [ThreadStatic]
    private static bool _completing;

    [Theory, InlineData(false), InlineData(true)]
    public async Task LateObservationPreservesTheFirstResultAndTaskIdentity(bool result)
    {
        var signal = new BatchCompletionSignal();
        Assert.True(signal.TrySetResult(result));
        Assert.False(signal.TrySetResult(!result));
        Assert.False(signal.TrySetException(new IOException("late failure")));
        Assert.Equal(result, await signal.Task);
        Assert.Same(signal.Task, signal.Task);
    }

    [Fact]
    public async Task StatusPollingDoesNotAllocateOrMaterializeAPendingPromise()
    {
        var signal = new BatchCompletionSignal();
        // Warm the getter before isolating its steady-state status checks.
        Assert.False(signal.IsCompleted);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var completed = false;
        for (var i = 0; i < 1024; i++)
            completed |= signal.IsCompleted;
        var pollingAllocation = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.False(completed);
        Assert.Equal(0, pollingAllocation);

        Assert.True(signal.TrySetResult());
        Assert.True(signal.IsCompleted);
        // Had status polling created a pending source, its completed task would
        // be a distinct allocation instead of the cached completed false task.
        Assert.Same(Task.FromResult(false), signal.Task);
        Assert.False(await signal.Task);
    }

    [Fact]
    public async Task ExplicitPendingTaskKeepsItsIdentityAndFirstCompletedOutcome()
    {
        var signal = new BatchCompletionSignal();
        var pending = signal.Task;
        Assert.Same(pending, signal.Task);
        Assert.False(signal.IsCompleted);
        Assert.False(pending.IsCompleted);
        Assert.True(signal.TrySetResult(true));
        Assert.True(signal.IsCompleted);
        Assert.False(signal.TrySetException(new IOException("late failure")));
        Assert.False(signal.TrySetResult(false));
        Assert.Same(pending, signal.Task);
        Assert.True(await pending);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task EarlyAndLateFaultsKeepTheOriginalError(bool earlyObserver)
    {
        var signal = new BatchCompletionSignal();
        var pending = earlyObserver ? signal.Task : null;
        Assert.False(signal.IsCompleted);
        var error = new IOException("original failure");
        Assert.True(signal.TrySetException(error));
        Assert.True(signal.IsCompleted);
        Assert.False(signal.TrySetResult());
        Assert.False(signal.TrySetException(new IOException("later failure")));
        Assert.Same(error, await Assert.ThrowsAsync<IOException>(async () => await signal.Task));
        if (pending is not null)
        {
            Assert.Same(pending, signal.Task);
        }
    }

    [Fact]
    public async Task AwaitersNeverRunInlineWithConfirmation()
    {
        var signal = new BatchCompletionSignal();
        var pending = signal.Task;
        var inline = false;
        Task observer = pending.ContinueWith
        (
            _ => inline = _completing, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default
        );
        _completing = true;
        try { Assert.True(signal.TrySetResult(true)); }
        finally { _completing = false; }
        Assert.True(await pending);
        await observer;
        Assert.False(inline);
    }
}
