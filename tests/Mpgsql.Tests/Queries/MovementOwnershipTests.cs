using System.Buffers;
using Mpgsql.Internal;
using Mpgsql.Protocol;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class MovementOwnershipTests
{
    [Theory, InlineData(false), InlineData(true)]
    public async Task OwnerObserversWaitUntilMovementOrDisposalReleasesBorrowedOwnership(bool dispose)
    {
        var token = TestContext.Current.CancellationToken;
        await using var wire = new ScriptedSession();
        await using var batch = wire.Session.CreateBatch(token);
        var owner = new TestOwner(true);
        var (reader, budget) = await OpenOwnedReaderAsync(wire, batch, owner, token);
        await using (reader)
        {
            var movement = dispose
                ? Task.Run
                (
                    () => reader
                        .DisposeAsync()
                        .AsTask(), token
                )
                : Task.Run(async () => { await reader.ReadAsync(); }, token);
            Task? first = null;
            Task? second = null;
            try
            {
                await owner.Entered.WaitAsync(TestTimeout, token);
                if (!dispose)
                {
                    await Assert.ThrowsAsync<InvalidOperationException>
                    (() => reader
                        .ReadAsync()
                        .AsTask()
                    );
                }
                first = reader.InvalidateFromOwner();
                second = reader.InvalidateFromOwner();
                Assert.Same(first, second);
                Assert.False(first.IsCompleted);
                Assert.Equal(14, budget.Used);
                Assert.Throws<ObjectDisposedException>(() => reader.GetRawValue(0));
                await Assert.ThrowsAsync<ObjectDisposedException>
                (() => reader
                    .ReadAsync()
                    .AsTask()
                );
            }
            finally { owner.Resume(); }
            await wire.WriteAsync(Join(Command(), Ready()));
            var error = await Record.ExceptionAsync(() => movement.WaitAsync(TestTimeout, token));
            if (dispose)
            {
                Assert.Null(error);
            }
            else
            {
                Assert.IsType<ObjectDisposedException>(error);
            }
            await Task
                .WhenAll(first!, second!)
                .WaitAsync(TestTimeout, token);
            Assert.Equal(1, owner.Disposals);
            Assert.Equal(0, budget.Used);
            Assert.True
            (
                reader.InvalidateFromOwner()
                    .IsCompletedSuccessfully
            );
            await batch.Completion.WaitAsync(TestTimeout, token);
            await FollowingAsync(wire, token);
        }
    }

    [Fact]
    public async Task ClosingAnIdleReaderKeepsConcurrentOwnerObserversPendingDuringCleanup()
    {
        var token = TestContext.Current.CancellationToken;
        await using var wire = new ScriptedSession();
        await using var batch = wire.Session.CreateBatch(token);
        var owner = new TestOwner(true);
        var (reader, budget) = await OpenOwnedReaderAsync(wire, batch, owner, token);
        await using (reader)
        {
            var closing = Task.Run(() => reader.InvalidateFromOwner(), token);
            Task? observer = null;
            try
            {
                await owner.Entered.WaitAsync(TestTimeout, token);
                var observerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                observer = Task.Run
                (
                    async () =>
                    {
                        observerEntered.TrySetResult();
                        await reader.InvalidateFromOwner();
                    }, token
                );
                await observerEntered.Task.WaitAsync(TestTimeout, token);
                Assert.False(closing.IsCompleted);
                Assert.False(observer.IsCompleted);
                Assert.Equal(14, budget.Used);
            }
            finally { owner.Resume(); }
            await Task
                .WhenAll(closing, observer!)
                .WaitAsync(TestTimeout, token);
            Assert.Equal(1, owner.Disposals);
            Assert.Equal(0, budget.Used);
            await Assert.ThrowsAsync<ObjectDisposedException>
            (() => reader
                .NextResultAsync()
                .AsTask()
            );
            await wire.WriteAsync(Join(Command(), Ready()));
            await batch.Completion.WaitAsync(TestTimeout, token);
            await FollowingAsync(wire, token);
        }
    }

    [Fact]
    public async Task MovementAndOwnerInvalidationRacesPreserveOneReleaseAndFollowingQueries()
    {
        var token = TestContext.Current.CancellationToken;
        await using var wire = new ScriptedSession();
        for (var iteration = 0;
             iteration < 128;
             iteration++)
        {
            await using var batch = wire.Session.CreateBatch(token);
            var owner = new TestOwner();
            var (reader, budget) = await OpenOwnedReaderAsync(wire, batch, owner, token);
            await using (reader)
            {
                using var start = new Barrier(3);
                var movement = Task.Run
                (
                    async () =>
                    {
                        Assert.True(start.SignalAndWait(TestTimeout, token));
                        try { Assert.False(await reader.ReadAsync()); }
                        catch (ObjectDisposedException) { }
                    }, token
                );
                var closing = Task.Run
                (
                    async () =>
                    {
                        Assert.True(start.SignalAndWait(TestTimeout, token));
                        await reader.InvalidateFromOwner();
                    }, token
                );
                Assert.True(start.SignalAndWait(TestTimeout, token));
                await wire.WriteAsync(Join(Command(), Ready()));
                await Task
                    .WhenAll(movement, closing)
                    .WaitAsync(TestTimeout, token);
                Assert.Equal(1, owner.Disposals);
                Assert.Equal(0, budget.Used);
                await Assert.ThrowsAsync<ObjectDisposedException>
                (() => reader
                    .ReadAsync()
                    .AsTask()
                );
                await batch.Completion.WaitAsync(TestTimeout, token);
                Assert.Equal(0, wire.Session.BufferedRowBytes);
                Assert.True(wire.Session.IsIdleAndHealthy);
            }
        }
        await FollowingAsync(wire, token);
    }

    private static async Task<(MpgsqlResultReader Reader, RowBufferBudget Budget)> OpenOwnedReaderAsync(
        ScriptedSession wire, MpgsqlQueryBatch batch,
        TestOwner owner, CancellationToken token
    )
    {
        await batch.SendQueryAsync("select owned row");
        await batch.SendSyncAsync();
        await wire.ReadOutputAsync();
        await wire.WriteAsync(Begin(20));
        var reader = await batch
            .ReadResultsAsync()
            .AsTask()
            .WaitAsync(TestTimeout, token);
        var budget = new RowBufferBudget(14);
        Assert.True(await budget.ReserveAsync(14, batch, token));
        IMemoryOwner<byte>? transferred = owner;
        var reservation = budget;
        batch.Accept
        (
            new BackendMessage((byte)'D', BackendMessageKind.DataRow, new ReadOnlySequence<byte>(owner.Memory), 1),
            ref transferred, ref reservation
        );
        Assert.Null(transferred);
        Assert.Null(reservation);
        Assert.True(await reader.ReadAsync());
        Assert.Equal(7, reader.GetInt64(0));
        return (reader, budget);
    }

    private static async Task FollowingAsync(ScriptedSession wire, CancellationToken token)
    {
        Assert.Equal(0, wire.Session.BufferedRowBytes);
        Assert.True(wire.Session.IsIdleAndHealthy);
        await using var batch = wire.Session.CreateBatch(token);
        await batch.SendQueryAsync("select following");
        await batch.SendSyncAsync();
        await wire.ReadOutputAsync();
        await wire.WriteAsync(Join(Query(777), Ready()));
        await using var reader = await batch.ReadResultsAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(777, reader.GetInt64(0));
        Assert.False(await reader.NextResultAsync());
        await batch.Completion.WaitAsync(TestTimeout, token);
        Assert.Equal(0, wire.Session.BufferedRowBytes);
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    private sealed class TestOwner(bool blockDispose = false) : IMemoryOwner<byte>
    {
        private readonly byte[] _bytes = Row(Int64(7))
            .AsSpan(5)
            .ToArray();

        private readonly TaskCompletionSource _entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim? _resume = blockDispose ? new ManualResetEventSlim() : null;
        private int _disposals;
        internal int Disposals => Volatile.Read(ref _disposals);
        internal Task Entered => _entered.Task;
        public Memory<byte> Memory => _bytes;
        public void Dispose()
        {
            Interlocked.Increment(ref _disposals);
            if (!blockDispose)
            {
                return;
            }
            _entered.TrySetResult();
            try
            {
                if (!_resume!.Wait(TestTimeout))
                {
                    throw new TimeoutException("row owner was not released");
                }
            }
            finally { _resume!.Dispose(); }
        }
        internal void Resume()
        {
            _resume?.Set();
        }
    }
}