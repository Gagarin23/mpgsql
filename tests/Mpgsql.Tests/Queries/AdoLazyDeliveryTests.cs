using System.IO.Pipelines;
using System.Reflection;
using Mpgsql.Internal;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class AdoLazyDeliveryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CompletedWithoutAnEarlyWaitUsesSharedPhysicalSuccess()
    {
        await using var wire = new DeliveryWire();
        var batch = wire.Session.CreateBatch(Token);
        var one = new OutboundWork(batch, new QueryDefinition("select 1", default));
        var two = new OutboundWork(batch, new QueryDefinition("select 2", default));
        one.Complete();
        two.Complete();
        await one.Completion;
        await two.Completion;
        Assert.True(one.Delivery.IsCompletedSuccessfully);
        Assert.Same(one.Delivery, two.Delivery);
        Assert.NotSame(one.Completion, one.Delivery);
        Assert.NotSame(two.Completion, two.Delivery);
    }

    [Fact]
    public async Task EarlyPhysicalWaitStaysPendingAfterManualCancellationWithoutToken()
    {
        await using var wire = new DeliveryWire();
        // The request must have no cancellation token to cover Cancel()'s separate barrier.
#pragma warning disable xUnit1051
        var batch = wire.Session.CreateBatch();
#pragma warning restore xUnit1051
        var work = new OutboundWork(batch, new QueryDefinition("select 1", default));
        var delivery = work.Delivery;
        Assert.Same(delivery, work.Delivery);
        Assert.NotSame(work.Completion, delivery);
        Assert.True(work.TryStart());
        work.Published();
        work.Cancel();
        var cancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => work.Completion.WaitAsync(TestTimeout, Token));
        Assert.False(cancellation.CancellationToken.CanBeCanceled);
        Assert.False(delivery.IsCompleted);
        work.Complete();
        await delivery.WaitAsync(TestTimeout, Token);
        Assert.Same(delivery, work.Delivery);
        Assert.True(work.Completion.IsCanceled);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task FirstWaitRacingCompletionNeverLosesItsAcknowledgement(bool fail)
    {
        await using var wire = new DeliveryWire();
        var batch = wire.Session.CreateBatch(Token);
        for (var iteration = 0; iteration < 64; iteration++)
        {
            var work = new OutboundWork(batch, new QueryDefinition("select 1", default));
            var failure = fail ? new IOException("Physical flush failure.") : null;
            using var start = new ManualResetEventSlim();
            var waiting = Task.Factory.StartNew(() =>
            {
                start.Wait(Token);
                return work.Delivery;
            }, Token, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
            var completing = Task.Run(() =>
            {
                start.Wait(Token);
                work.Complete(failure);
            }, Token);
            start.Set();
            await Task.WhenAll(waiting, completing).WaitAsync(TestTimeout, Token);
            var delivery = await waiting;
            Assert.Same(delivery, work.Delivery);
            if (failure is null)
            {
                await delivery.WaitAsync(TestTimeout, Token);
                await work.Completion.WaitAsync(TestTimeout, Token);
            }
            else
            {
                Assert.Same(failure, await Record.ExceptionAsync(() => delivery.WaitAsync(TestTimeout, Token)));
                Assert.Same(failure, await Record.ExceptionAsync(() => work.Completion.WaitAsync(TestTimeout, Token)));
            }
        }
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task FailureBeforeFirstWaitKeepsOriginalFaultAndDoesNotPoisonAnotherWork(bool cancelledError)
    {
        await using var wire = new DeliveryWire();
        var batch = wire.Session.CreateBatch(Token);
        Exception failure = cancelledError ? new OperationCanceledException("Physical flush stopped.")
            : new IOException("Physical flush failed.");
        var failed = new OutboundWork(batch, new QueryDefinition("select 1", default));
        failed.FailQueued(failure);
        var delivery = failed.Delivery;
        Assert.Same(failure, await Record.ExceptionAsync(() => delivery.WaitAsync(TestTimeout, Token)));
        Assert.Same(failure, await Record.ExceptionAsync(() => failed.Completion.WaitAsync(TestTimeout, Token)));
        Assert.True(delivery.IsFaulted);
        Assert.False(delivery.IsCanceled);
        failed.Complete();
        Assert.Same(delivery, failed.Delivery);
        Assert.True(delivery.IsFaulted);
        var following = new OutboundWork(batch, new QueryDefinition("select 2", default));
        var followingDelivery = following.Delivery;
        Assert.False(followingDelivery.IsCompleted);
        following.Complete();
        await followingDelivery.WaitAsync(TestTimeout, Token);
        await following.Completion.WaitAsync(TestTimeout, Token);
    }

    [Fact]
    public async Task InputFlushCanReleaseProducerBeforeTheSyncDeliveryWaitIsCreated()
    {
        await using var wire = new DeliveryWire();
        var batch = wire.Session.CreateBatch(Token);
        var work = new OutboundWork(batch, new[] { new QueryDefinition("select 1", default) }, sync: true);
        Assert.True(work.TryStart());
        Assert.True(work.TryGetQuery(out _));
        work.QueryPublished();
        work.PrepareInputFlush();
        work.PauseQueryGroup();
        work.InputsFlushed();
        await work.Completion.WaitAsync(TestTimeout, Token);
        var delivery = work.Delivery;
        Assert.False(delivery.IsCompleted);
        Assert.True(work.TryStart());
        Assert.False(work.TryGetQuery(out _));
        work.Published();
        work.Complete();
        await delivery.WaitAsync(TestTimeout, Token);
        Assert.Same(delivery, work.Delivery);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task GeneralAcknowledgementIdentitySurvivesAdoFactoryHandoff(bool cancellable)
    {
        await using var wire = new DeliveryWire(ado: false);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(Token);
#pragma warning disable xUnit1051
        var batch = wire.Session.CreateBatch(cancellable ? request.Token : default);
#pragma warning restore xUnit1051
        var work = new OutboundWork(batch, new QueryDefinition("select 1", default));
        var delivery = work.Delivery;
        if (cancellable)
            Assert.NotSame(work.Completion, delivery);
        else
            Assert.Same(work.Completion, delivery);
        work.Complete();
        await work.Completion.WaitAsync(TestTimeout, Token);
        await delivery.WaitAsync(TestTimeout, Token);
        batch.CompleteIfUnpublished();
        await batch.DisposeAsync();
        Assert.True(wire.Session.IsIdleAndHealthy);
        await wire.Session.ClaimForAdoDataSourceAsync(8 * 1024 * 1024, Token);
        Assert.True(wire.Session.IsAdoSession);
        Assert.Same(delivery, work.Delivery);
    }

    private sealed class DeliveryWire : IAsyncDisposable
    {
        private readonly Pipe _incoming = new(new PipeOptions(useSynchronizationContext: false));
        private readonly Pipe _outgoing = new(new PipeOptions(useSynchronizationContext: false));
        internal MpgsqlMessageSession Session { get; }
        internal DeliveryWire(bool ado = true)
        {
            if (ado)
            {
                var constructor = typeof(MpgsqlMessageSession).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                    .Single(candidate => candidate.GetParameters().Length == 5);
                Session = (MpgsqlMessageSession)constructor.Invoke([_incoming.Reader, _outgoing.Writer, Token, null, true]);
            }
            else
                Session = new MpgsqlMessageSession(_incoming.Reader, _outgoing.Writer, Token);
        }
        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync().AsTask().WaitAsync(TestTimeout, Token);
            await _incoming.Writer.CompleteAsync();
            await _outgoing.Reader.CompleteAsync();
        }
    }
}
