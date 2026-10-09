using System.Buffers;
using System.Reflection;
using Mpgsql.Internal;
using Mpgsql.Protocol;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class RowGenerationContractTests
{
    private static readonly FieldInfo Generation = typeof(RowStorage).GetField("_generation", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static BackendMessage Message(CountingOwner owner)
        => new BackendMessage((byte)'D', BackendMessageKind.DataRow, new ReadOnlySequence<byte>(owner.Memory), 1);

    private static CountingOwner Owner(long value) => new CountingOwner(Row(Int64(value)).AsSpan(5).ToArray());

    [Theory, InlineData(false), InlineData(true)]
    public void FinalReusableGenerationsRetireWithoutWrappingOrAliasingStaleHandles(bool singleRenter)
    {
        var pool = new RowStoragePool(singleRenter);
        var storage = new RowStorage(pool);
        // Seed a free even generation: reaching the retirement edge naturally is impractical.
        Generation.SetValue(storage, long.MaxValue - 5);
        var firstOwner = Owner(7);
        var first = new OwnedRow(storage, Message(firstOwner), firstOwner, null);
        Assert.Equal(long.MaxValue - 4, (long)Generation.GetValue(storage)!);
        var stale = first;
        first.Dispose();
        Assert.Equal(long.MaxValue - 3, (long)Generation.GetValue(storage)!);
        Assert.Equal(1, firstOwner.Disposals);
        Assert.True(storage.CanReuse);
        Assert.Equal(1, pool.RetainedStorageCount);

        var finalOwner = Owner(99);
        var final = pool.Rent(Message(finalOwner), finalOwner, null);
        Assert.Equal(long.MaxValue - 2, (long)Generation.GetValue(storage)!);
        stale.Dispose();
        Assert.Equal(long.MaxValue - 2, (long)Generation.GetValue(storage)!);
        Assert.Equal(0, finalOwner.Disposals);
        Assert.Throws<ObjectDisposedException>(() => stale[0]);
        Assert.Equal(Int64(99), final[0]!.Value.ToArray());
        final.Dispose();
        Assert.Equal(long.MaxValue - 1, (long)Generation.GetValue(storage)!);
        Assert.False(storage.CanReuse);
        Assert.Equal(0, pool.RetainedStorageCount);
        Assert.Equal(1, finalOwner.Disposals);
        final.Dispose();
        stale.Dispose();
        Assert.Equal(long.MaxValue - 1, (long)Generation.GetValue(storage)!);

        var followingOwner = Owner(123);
        using var following = pool.Rent(Message(followingOwner), followingOwner, null);
        Assert.Equal(long.MaxValue - 1, (long)Generation.GetValue(storage)!);
        Assert.Equal(Int64(123), following[0]!.Value.ToArray());
        Assert.Equal(0, followingOwner.Disposals);
        following.Dispose();
        Assert.Equal(1, followingOwner.Disposals);
        Assert.Equal(1, pool.RetainedStorageCount);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task StaleDisposalRacingNewLeasesCannotReleaseTheirOwnerOrBudget(bool singleRenter)
    {
        await using var wire = new ScriptedSession();
        await using var batch = wire.Session.CreateBatch(Token);
        var pool = new RowStoragePool(singleRenter);
        var budget = new RowBufferBudget(14);
        var initialOwner = Owner(0);
        var stale = pool.Rent(Message(initialOwner), initialOwner, null);
        stale.Dispose();
        var stop = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposer = Task.Run
        (
            () =>
            {
                started.TrySetResult();
                while (Volatile.Read(ref stop) == 0)
                {
                    stale.Dispose();
                }
            }, Token
        );
        try
        {
            await started.Task.WaitAsync(TestTimeout, Token);
            for (var value = 1; value <= 512; value++)
            {
                var owner = Owner(value);
                Assert.True(await budget.ReserveAsync(owner.Memory.Length, batch, Token));
                var row = pool.Rent(Message(owner), owner, budget);
                try
                {
                    stale.Dispose();
                    Assert.Equal(Int64(value), row[0]!.Value.ToArray());
                    Assert.Equal(0, owner.Disposals);
                    Assert.Equal(14, budget.Used);
                }
                finally { row.Dispose(); }
                Assert.Equal(1, owner.Disposals);
                Assert.Equal(0, budget.Used);
            }
        }
        finally
        {
            Volatile.Write(ref stop, 1);
            await disposer.WaitAsync(TestTimeout, Token);
        }
        Assert.Equal(1, initialOwner.Disposals);
        Assert.Throws<ObjectDisposedException>(() => stale[0]);
        Assert.Equal(1, pool.RetainedStorageCount);
    }

    private sealed class CountingOwner(byte[] bytes) : IMemoryOwner<byte>
    {
        private int _disposals;
        internal int Disposals => Volatile.Read(ref _disposals);
        public Memory<byte> Memory => bytes;
        public void Dispose() => Interlocked.Increment(ref _disposals);
    }
}
