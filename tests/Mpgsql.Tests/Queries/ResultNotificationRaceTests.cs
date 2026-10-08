using System.Threading.Channels;
using Mpgsql.Internal;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class ResultNotificationRaceTests
{
    [ThreadStatic] private static bool _publishing;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentNotificationAndImmediateSourceReuseKeepEveryEvent(bool deferred)
    {
        var token = TestContext.Current.CancellationToken;
        var buffer = new ResultEventBuffer();
        var requests = Channel.CreateUnbounded<int>(new() { SingleReader = true, SingleWriter = true });
        const int iterations = 4096;
        var producer = Task.Run(async () =>
        {
            await foreach (int index in requests.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                _publishing = true;
                try
                {
                    Assert.True(buffer.TryWrite(new(index, default), notify: !deferred));
                    if (deferred) buffer.NotifyAvailable();
                }
                finally { _publishing = false; }
            }
        }, token);
        try
        {
            // Keep the consumer off the test synchronization context: otherwise that context
            // could hide an inline source continuation and make the publisher assertion vacuous.
            await Task.Run(async () =>
            {
                for (int index = 0; index < iterations; index++)
                {
                    var notification = buffer.WaitToReadAsync();
                    Assert.False(notification.IsCompleted);
                    Assert.True(requests.Writer.TryWrite(index));
                    Assert.True(await notification.ConfigureAwait(false));
                    Assert.False(_publishing); // the consumer never runs inline inside the publisher
                    Assert.True(buffer.TryRead(out var item));
                    Assert.Equal(index, item.QueryIndex);
                    Assert.False(buffer.TryRead(out _));
                }
                var terminal = buffer.WaitToReadAsync();
                buffer.Complete();
                Assert.False(await terminal.ConfigureAwait(false));
            }, token).WaitAsync(TestTimeout, token);
        }
        finally
        {
            requests.Writer.TryComplete();
            await producer.WaitAsync(TestTimeout, token);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NotificationRacingFaultAndDrainPreservesExactlyOneTerminalError(bool deferred)
    {
        var token = TestContext.Current.CancellationToken;
        for (int iteration = 0; iteration < 128; iteration++)
        {
            var buffer = new ResultEventBuffer();
            var notification = buffer.WaitToReadAsync().AsTask();
            var error = new IOException("racing transport failure");
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var producer = Task.Run(async () =>
            {
                await start.Task.ConfigureAwait(false);
                if (buffer.TryWrite(new(iteration, default), notify: !deferred) && deferred)
                    buffer.NotifyAvailable();
            }, token);
            var terminal = Task.Run(async () =>
            {
                await start.Task.ConfigureAwait(false);
                buffer.Complete(error);
                buffer.Drain();
                buffer.Complete(); // normal completion cannot erase the transport failure
            }, token);
            start.SetResult();
            await Task.WhenAll(producer, terminal).WaitAsync(TestTimeout, token);
            try { Assert.True(await notification.WaitAsync(TestTimeout, token)); }
            catch (IOException observed) { Assert.Same(error, observed); }
            buffer.Drain();
            Assert.False(buffer.TryRead(out _));
            Assert.Same(error, await Assert.ThrowsAsync<IOException>(async () => await buffer.WaitToReadAsync()));
        }
    }
}
