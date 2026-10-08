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

    [Theory, InlineData(false), InlineData(true)]
    public async Task EarlyAndLateFaultsKeepTheOriginalError(bool earlyObserver)
    {
        var signal = new BatchCompletionSignal();
        var pending = earlyObserver ? signal.Task : null;
        var error = new IOException("original failure");
        Assert.True(signal.TrySetException(error));
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