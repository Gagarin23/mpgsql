using System.Buffers;
using Mpgsql.Internal;
using Mpgsql.Protocol;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class OwnedRowLeaseTests
{
    private static BackendMessage Message(params byte[]?[] values)
        => new((byte)'D', BackendMessageKind.DataRow, new(Row(values).AsMemory(5)), values.Length);

    [Fact]
    public async Task OldCopiesCannotReleaseOrReadAReusedStorageGeneration()
    {
        await using var wire = new ScriptedSession();
        await using var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        var pool = new RowStoragePool();
        var budget = new RowBufferBudget(28);
        var one = Message(Int64(11));
        Assert.True(await budget.ReserveAsync(one.Payload.Length, batch, TestContext.Current.CancellationToken));
        var row = pool.Rent(one, null, budget);
        var stale = row;
        row.Dispose();
        Assert.Equal(0, budget.Used);
        var two = Message(Int64(22));
        Assert.True(await budget.ReserveAsync(two.Payload.Length, batch, TestContext.Current.CancellationToken));
        var next = pool.Rent(two, null, budget);
        stale.Dispose(); row.Dispose();
        Assert.Equal(two.Payload.Length, budget.Used);
        Assert.Throws<ObjectDisposedException>(() => stale[0]);
        Assert.Equal(Int64(22), next[0]!.Value.ToArray());
        next.Dispose(); next.Dispose();
        Assert.Equal(0, budget.Used);
    }

    [Fact]
    public async Task ConcurrentCopiesReleaseFrameAndBudgetExactlyOnce()
    {
        await using var wire = new ScriptedSession();
        await using var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        var message = Message(Int64(33));
        var owner = new CountingOwner(message.Payload.ToArray());
        var budget = new RowBufferBudget(14);
        Assert.True(await budget.ReserveAsync(14, batch, TestContext.Current.CancellationToken));
        var pool = new RowStoragePool();
        var row = pool.Rent(new((byte)'D', BackendMessageKind.DataRow, new(owner.Memory), 1), owner, budget);
        await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(row.Dispose, TestContext.Current.CancellationToken)))
            .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.Equal(1, owner.Disposals);
        Assert.Equal(0, budget.Used);
        var next = pool.Rent(message, null, null);
        row.Dispose();
        Assert.Equal(Int64(33), next[0]!.Value.ToArray());
        next.Dispose();
    }

    [Fact]
    public void NullEmptyFieldOffsetsAndRepeatedRentalPreserveBytes()
    {
        var pool = new RowStoragePool();
        for (int iteration = 0; iteration < 64; iteration++)
        {
            byte[] value = [(byte)iteration, 1, 2, 3];
            using var row = pool.Rent(Message(null, [], Int64(iteration), value, null), null, null);
            Assert.Null(row[0]);
            Assert.True(row[1]!.Value.IsEmpty);
            Assert.Equal(Int64(iteration), row[2]!.Value.ToArray());
            Assert.Equal(value, row[3]!.Value.ToArray());
            Assert.Null(row[4]);
            Assert.Throws<ArgumentOutOfRangeException>(() => row[-1]);
            Assert.Throws<ArgumentOutOfRangeException>(() => row[5]);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(129)]
    public void ReusedStorageHandlesChangingFieldCountsAndNullEmptyValues(int fields)
    {
        var pool = new RowStoragePool();
        for (int iteration = 0; iteration < 9; iteration++)
        {
            foreach (int count in new[] { fields, 1, 0, fields })
            {
                var values = new byte[]?[count];
                for (int field = 0; field < count; field++)
                    values[field] = ((iteration + field) % 3) switch
                    {
                        0 => null,
                        1 => [],
                        _ => Int64(iteration * 256L + field)
                    };
                using var row = pool.Rent(Message(values), null, null);
                for (int field = 0; field < count; field++)
                {
                    if (values[field] is { } expected)
                        Assert.Equal(expected, row[field]!.Value.ToArray());
                    else Assert.Null(row[field]);
                }
                Assert.Throws<ArgumentOutOfRangeException>(() => row[count]);
            }
        }
    }

    [Theory]
    [InlineData(0, 0, 0, 8)] // field extends past the frame
    [InlineData(255, 255, 255, 254)] // illegal negative field length
    [InlineData(0, 0, 0, 0)] // trailing byte after an empty field
    public void InitializationFailureLeavesFrameOwnershipWithTheCaller(byte a, byte b, byte c, byte d)
    {
        var pool = new RowStoragePool();
        var owner = new CountingOwner([0, 1, a, b, c, d, 99]);
        var malformed = new BackendMessage((byte)'D', BackendMessageKind.DataRow, new(owner.Memory), 1);
        Assert.Throws<InvalidDataException>(() => pool.Rent(malformed, owner, null));
        Assert.Equal(0, owner.Disposals);
        using var next = pool.Rent(Message(Int64(7)), null, null);
        Assert.Equal(Int64(7), next[0]!.Value.ToArray());
        owner.Dispose();
    }

    [Fact]
    public void WarmSingleRowRentalsDoNotAllocatePerRow()
    {
        var pool = new RowStoragePool();
        var message = Message(Int64(42));
        for (int i = 0; i < 64; i++) { var warm = pool.Rent(message, null, null); _ = warm[0]; warm.Dispose(); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) { var row = pool.Rent(message, null, null); _ = row[0]; row.Dispose(); }
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    private sealed class CountingOwner(byte[] bytes) : IMemoryOwner<byte>
    {
        private int _disposals;
        internal int Disposals => Volatile.Read(ref _disposals);
        public Memory<byte> Memory => bytes;
        public void Dispose() => Interlocked.Increment(ref _disposals);
    }
}
