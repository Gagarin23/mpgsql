using System.Buffers;
using Mpgsql.Internal;
using Mpgsql.Protocol;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class RowStoragePoolConcurrencyTests
{
    [Fact]
    public async Task ConcurrentRentalsAndReturnsKeepEveryOutstandingLeaseDistinct()
    {
        var pool = new RowStoragePool();
        var workers = Enumerable.Range(0, 8).Select(worker => Task.Run(async () =>
        {
            for (int iteration = 0; iteration < 128; iteration++)
            {
                var rows = new OwnedRow[16];
                var owners = new CountingOwner[16];
                var expected = new byte[16][];
                try
                {
                    for (int i = 0; i < rows.Length; i++)
                    {
                        expected[i] = Int64(worker * 100000L + iteration * 16 + i);
                        owners[i] = new(Row(expected[i], null, []).AsSpan(5).ToArray());
                        rows[i] = pool.Rent(new((byte)'D', BackendMessageKind.DataRow,
                            new(owners[i].Memory), 3), owners[i], null);
                    }
                    // Other workers can release and reacquire storage while these leases stay live.
                    await Task.Yield();
                    for (int i = 0; i < rows.Length; i++)
                    {
                        Assert.Equal(expected[i], rows[i][0]!.Value.ToArray());
                        Assert.Null(rows[i][1]);
                        Assert.True(rows[i][2]!.Value.IsEmpty);
                    }
                }
                finally
                {
                    for (int i = rows.Length - 1; i >= 0; i--) rows[i].Dispose();
                }
                for (int i = 0; i < rows.Length; i++)
                {
                    rows[i].Dispose();
                    Assert.Equal(1, owners[i].Disposals);
                    Assert.Throws<ObjectDisposedException>(() => rows[i][0]);
                }
            }
        }, TestContext.Current.CancellationToken)).ToArray();
        await Task.WhenAll(workers).WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ThrowingFrameOwnerStillReleasesBudgetAndKeepsLaterLeaseUsable()
    {
        await using var wire = new ScriptedSession();
        await using var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        var pool = new RowStoragePool();
        var budget = new RowBufferBudget(14);
        var owner = new CountingOwner(Row(Int64(7)).AsSpan(5).ToArray(), throwOnDispose: true);
        Assert.True(await budget.ReserveAsync(owner.Memory.Length, batch, TestContext.Current.CancellationToken));
        var row = pool.Rent(new((byte)'D', BackendMessageKind.DataRow, new(owner.Memory), 1), owner, budget);
        var stale = row;
        Assert.Throws<IOException>(row.Dispose);
        Assert.Equal(1, owner.Disposals);
        Assert.Equal(0, budget.Used);
        var following = new CountingOwner(Row(Int64(99)).AsSpan(5).ToArray());
        Assert.True(await budget.ReserveAsync(following.Memory.Length, batch, TestContext.Current.CancellationToken));
        var next = pool.Rent(new((byte)'D', BackendMessageKind.DataRow, new(following.Memory), 1), following, budget);
        stale.Dispose();
        Assert.Equal(0, following.Disposals);
        Assert.Equal(14, budget.Used);
        Assert.Throws<ObjectDisposedException>(() => stale[0]);
        Assert.Equal(Int64(99), next[0]!.Value.ToArray());
        next.Dispose();
        Assert.Equal(1, following.Disposals);
        Assert.Equal(0, budget.Used);
    }

    private sealed class CountingOwner(byte[] bytes, bool throwOnDispose = false) : IMemoryOwner<byte>
    {
        private int _disposals;
        internal int Disposals => Volatile.Read(ref _disposals);
        public Memory<byte> Memory => bytes;
        public void Dispose()
        {
            Interlocked.Increment(ref _disposals);
            if (throwOnDispose) throw new IOException("frame owner disposal failed");
        }
    }
}
