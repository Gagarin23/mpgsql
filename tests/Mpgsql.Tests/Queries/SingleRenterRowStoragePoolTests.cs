using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Mpgsql.Internal;
using Mpgsql.Protocol;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class SingleRenterRowStoragePoolTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static OwnedRow Rent(RowStoragePool pool, CountingOwner owner)
    {
        return pool.Rent
        (
            new BackendMessage((byte)'D', BackendMessageKind.DataRow, new ReadOnlySequence<byte>(owner.Memory), 3),
            owner, null
        );
    }

    private static CountingOwner Owner(long value)
    {
        return new CountingOwner(Row(Int64(value), null, []).AsSpan(5).ToArray());
    }

    [Fact]
    public async Task OneRenterAndConcurrentReturnsKeepEveryLeaseAndFrameDistinct()
    {
        const int rentals = 4096;
        var pool = new RowStoragePool(singleRenter: true);
        var channel = Channel.CreateBounded<(OwnedRow Row, CountingOwner Owner, long Value)>
        (
            new BoundedChannelOptions(64)
            {
                SingleWriter = true,
                AllowSynchronousContinuations = false
            }
        );
        var consumed = 0;
        var consumers = Enumerable.Range(0, 8).Select(_ => Task.Run
        (
            async () =>
            {
                await foreach (var item in channel.Reader.ReadAllAsync(Token))
                {
                    try
                    {
                        Assert.Equal(Int64(item.Value), item.Row[0]!.Value.ToArray());
                        Assert.Null(item.Row[1]);
                        Assert.True(item.Row[2]!.Value.IsEmpty);
                    }
                    finally { item.Row.Dispose(); }
                    item.Row.Dispose();
                    Assert.Equal(1, item.Owner.Disposals);
                    Assert.Throws<ObjectDisposedException>(() => item.Row[0]);
                    Interlocked.Increment(ref consumed);
                }
            }, Token
        )).ToArray();
        var producer = Task.Run
        (
            async () =>
            {
                try
                {
                    for (var value = 0; value < rentals; value++)
                    {
                        var owner = Owner(value);
                        var row = Rent(pool, owner);
                        try { await channel.Writer.WriteAsync((row, owner, value), Token); }
                        catch
                        {
                            row.Dispose();
                            throw;
                        }
                    }
                }
                finally { channel.Writer.TryComplete(); }
            }, Token
        );
        await Task.WhenAll(consumers.Append(producer)).WaitAsync(TestTimeout, Token);
        Assert.Equal(rentals, consumed);
        Assert.InRange(pool.RetainedStorageCount, 1, 4127);
    }

    [Fact]
    public void DetachedRentalCacheAndConcurrentReturnListHaveBoundedCombinedCapacity()
    {
        var pool = new RowStoragePool(singleRenter: true);
        var rows = new OwnedRow[4200];
        var owners = new CountingOwner[rows.Length];
        for (var i = 0; i < rows.Length; i++)
        {
            owners[i] = Owner(i);
            rows[i] = Rent(pool, owners[i]);
        }
        for (var i = 0; i < 4096; i++)
        {
            rows[i].Dispose();
        }
        Assert.Equal(4096, pool.RetainedStorageCount);

        var nextOwner = Owner(9999);
        var next = Rent(pool, nextOwner); // One active rental, 31 cached, 4064 shared.
        Assert.Equal(4095, pool.RetainedStorageCount);
        for (var i = 4096; i < 4128; i++)
        {
            rows[i].Dispose();
        }
        Assert.Equal(4127, pool.RetainedStorageCount);
        next.Dispose();
        for (var i = 4128; i < rows.Length; i++)
        {
            rows[i].Dispose();
        }
        Assert.Equal(4127, pool.RetainedStorageCount);
        Assert.Equal(1, nextOwner.Disposals);
        foreach (var owner in owners)
        {
            Assert.Equal(1, owner.Disposals);
        }
        foreach (var stale in rows)
        {
            stale.Dispose();
            Assert.Throws<ObjectDisposedException>(() => stale[0]);
        }
        using var reused = Rent(pool, Owner(42));
        Assert.Equal(Int64(42), reused[0]!.Value.ToArray());
    }

    [Fact]
    public void SharedAndDetachedEmptyStorageDoNotRetainFrameOwnersOrPayloadArrays()
    {
        var pool = new RowStoragePool(singleRenter: true);
        var weak = PopulateEmptyCaches(pool);
        Assert.Equal(64, pool.RetainedStorageCount);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        foreach (var (owner, bytes) in weak)
        {
            Assert.False(owner.IsAlive);
            Assert.False(bytes.IsAlive);
        }
        GC.KeepAlive(pool);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Owner, WeakReference Bytes)[] PopulateEmptyCaches(RowStoragePool pool)
    {
        var rows = new OwnedRow[64];
        var weak = new (WeakReference, WeakReference)[rows.Length];
        for (var i = 0; i < rows.Length; i++)
        {
            var bytes = Row(Int64(i), null, []).AsSpan(5).ToArray();
            var owner = new CountingOwner(bytes);
            weak[i] = (new WeakReference(owner), new WeakReference(bytes));
            rows[i] = Rent(pool, owner);
        }
        foreach (var row in rows)
        {
            row.Dispose();
        }
        var detached = Rent(pool, Owner(42));
        detached.Dispose();
        return weak;
    }

    private sealed class CountingOwner(byte[] bytes) : IMemoryOwner<byte>
    {
        private int _disposals;
        internal int Disposals => Volatile.Read(ref _disposals);
        public Memory<byte> Memory => bytes;
        public void Dispose()
        {
            Interlocked.Increment(ref _disposals);
        }
    }
}
