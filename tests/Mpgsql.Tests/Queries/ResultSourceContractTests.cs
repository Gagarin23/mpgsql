using Mpgsql.Internal;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class ResultSourceContractTests
{
    [ThreadStatic] private static bool _publishing;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegistrationBeforeOrAfterCompletionPreservesContextAndSingleDelivery(bool completeFirst)
    {
        var token = TestContext.Current.CancellationToken;
        await Task.Run(async () =>
        {
            var buffer = new ResultEventBuffer();
            var context = new AsyncLocal<string?>();
            var scheduling = new RecordingContext();
            for (int index = 0; index < 128; index++)
            {
                var waiting = buffer.WaitToReadAsync();
                Assert.False(waiting.IsCompleted);
                var awaiter = waiting.GetAwaiter();
                var observed = new TaskCompletionSource<(bool Available, string? Context, bool Inline)>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                int delivered = 0;
                if (completeFirst) Publish(buffer, index);
                context.Value = $"registered-{index}";
                var previous = SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(scheduling);
                try
                {
                    // Safe OnCompleted requests both scheduling and execution context flow.
                    // This notification is consumed exactly once by this callback.
                    awaiter.OnCompleted(() =>
                    {
                        try
                        {
                            Interlocked.Increment(ref delivered);
                            observed.TrySetResult((awaiter.GetResult(), context.Value, _publishing));
                        }
                        catch (Exception error) { observed.TrySetException(error); }
                    });
                }
                finally { SynchronizationContext.SetSynchronizationContext(previous); }
                context.Value = $"caller-{index}";
                if (!completeFirst) Publish(buffer, index);
                var result = await observed.Task.WaitAsync(TestTimeout, token).ConfigureAwait(false);
                Assert.True(result.Available);
                Assert.Equal($"registered-{index}", result.Context);
                Assert.False(result.Inline);
                Assert.Equal(1, Volatile.Read(ref delivered));
                Assert.Equal(index + 1, scheduling.Posts);
                Assert.Equal($"caller-{index}", context.Value);
                Assert.True(buffer.TryRead(out var item));
                Assert.Equal(index, item.QueryIndex);
                Assert.False(buffer.TryRead(out _));
            }
            var terminal = buffer.WaitToReadAsync();
            buffer.Complete();
            Assert.False(await terminal.ConfigureAwait(false));
        }, token).WaitAsync(TestTimeout, token);
    }

    [Fact]
    public async Task PendingGetResultAndStaleCopiesDoNotReleaseTheNextWait()
    {
        var buffer = new ResultEventBuffer();
        var first = buffer.WaitToReadAsync();
        Assert.Throws<InvalidOperationException>(() => first.GetAwaiter().GetResult());
        Assert.Throws<InvalidOperationException>(() => buffer.WaitToReadAsync());
        Assert.True(buffer.TryWrite(new(11, default)));
        Assert.True(await first);
        Assert.True(buffer.TryRead(out var one));
        Assert.Equal(11, one.QueryIndex);

        var next = buffer.WaitToReadAsync();
        Assert.False(next.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => first.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => first.GetAwaiter().GetResult());
        Assert.Throws<InvalidOperationException>(() => first.GetAwaiter().UnsafeOnCompleted(() => { }));
        Assert.Throws<InvalidOperationException>(() => next.GetAwaiter().GetResult());
        Assert.Throws<InvalidOperationException>(() => buffer.WaitToReadAsync());
        Assert.True(buffer.TryWrite(new(22, default)));
        Assert.True(await next);
        Assert.True(buffer.TryRead(out var two));
        Assert.Equal(22, two.QueryIndex);
        Assert.False(buffer.TryRead(out _));
        var terminal = buffer.WaitToReadAsync();
        buffer.Complete();
        Assert.False(await terminal);
    }

    private static void Publish(ResultEventBuffer buffer, int index)
    {
        _publishing = true;
        try { Assert.True(buffer.TryWrite(new(index, default))); }
        finally { _publishing = false; }
    }

    private sealed class RecordingContext : SynchronizationContext
    {
        private int _posts;
        internal int Posts => Volatile.Read(ref _posts);
        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref _posts);
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }
    }
}
