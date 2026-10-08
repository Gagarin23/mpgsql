using Mpgsql.Internal;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class SessionWaiterTests
{
    [ThreadStatic] private static bool _publishing;

    [Fact]
    public async Task CompletionBeforeAwaitKeepsTheFirstOutcome()
    {
        await using var wire = new ScriptedSession();
        await using var source = new MpgsqlDataSource(_ => ValueTask.FromResult(wire.Session),
            (_, _) => ValueTask.CompletedTask);
        var session = new PooledSession(wire.Session);
        var waiter = new SessionWaiter(source, false, TestContext.Current.CancellationToken);
        Assert.True(waiter.TrySetResult(session));
        Assert.False(waiter.TrySetException(new IOException("late failure")));
        Assert.False(waiter.TrySetCanceled());
        Assert.Same(session, await waiter.WaitAsync());
    }

    [Fact]
    public async Task CancellationPreservesTheRequestTokenAndCannotBeOverwritten()
    {
        await using var wire = new ScriptedSession();
        await using var source = new MpgsqlDataSource(_ => ValueTask.FromResult(wire.Session),
            (_, _) => ValueTask.CompletedTask);
        using var request = new CancellationTokenSource();
        request.Cancel();
        var waiter = new SessionWaiter(source, false, request.Token);
        Assert.True(waiter.TrySetCanceled());
        Assert.False(waiter.TrySetResult(new(wire.Session)));
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiter.WaitAsync());
        Assert.Equal(request.Token, error.CancellationToken);
    }

    [Fact]
    public async Task RacingCompletionsHaveExactlyOneWinner()
    {
        await using var wire = new ScriptedSession();
        await using var source = new MpgsqlDataSource(_ => ValueTask.FromResult(wire.Session),
            (_, _) => ValueTask.CompletedTask);
        var session = new PooledSession(wire.Session);
        var failure = new IOException("factory failure");
        for (int i = 0; i < 128; i++)
        {
            var waiter = new SessionWaiter(source, false, TestContext.Current.CancellationToken);
            var pending = waiter.WaitAsync().AsTask();
            var success = Task.Run(() => waiter.TrySetResult(session), TestContext.Current.CancellationToken);
            var failed = Task.Run(() => waiter.TrySetException(failure), TestContext.Current.CancellationToken);
            bool[] outcomes = await Task.WhenAll(success, failed);
            Assert.NotEqual(outcomes[0], outcomes[1]);
            if (outcomes[0]) Assert.Same(session, await pending);
            else Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => pending));
        }
    }

    [Fact]
    public async Task AdmissionConsumerNeverContinuesOnThePublishingThread()
    {
        await using var wire = new ScriptedSession();
        await using var source = new MpgsqlDataSource(_ => ValueTask.FromResult(wire.Session),
            (_, _) => ValueTask.CompletedTask);
        var waiter = new SessionWaiter(source, false, TestContext.Current.CancellationToken);
        var pending = waiter.WaitAsync();
        var resumed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending.GetAwaiter().UnsafeOnCompleted(() => resumed.SetResult(_publishing));
        var session = new PooledSession(wire.Session);
        _publishing = true;
        try { Assert.True(waiter.TrySetResult(session)); }
        finally { _publishing = false; }
        Assert.False(await resumed.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
        Assert.Same(session, await pending);
    }
}
