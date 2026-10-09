using System.Buffers;
using System.Runtime.CompilerServices;
using Mpgsql.Internal;
using Mpgsql.Protocol;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class SmallRowBackingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static BackendMessage Message(params byte[]?[] values)
    {
        return Message(new ReadOnlySequence<byte>(Row(values).AsMemory(5)), values.Length);
    }

    private static BackendMessage Message(ReadOnlySequence<byte> payload, int fields)
    {
        return new BackendMessage((byte)'D', BackendMessageKind.DataRow, payload, fields);
    }

    [Theory, InlineData(0), InlineData(1), InlineData(128), InlineData(129)]
    public void EmptyScalarAndBoundaryRowsPreserveBytesAcrossRentalGenerations(int scenario)
    {
        var values = scenario switch
        {
            0 => Array.Empty<byte[]?>(),
            1 => new byte[]?[] { Int64(42) },
            _ => new byte[]?[] { Enumerable.Range(0, scenario - 6).Select(i => (byte)i).ToArray() }
        };
        var message = Message(values);
        if (scenario >= 128)
        {
            Assert.Equal(scenario, message.Payload.Length);
        }
        var pool = new RowStoragePool(singleRenter: true);
        for (var iteration = 0; iteration < 64; iteration++)
        {
            var row = pool.Rent(message, null, null);
            for (var field = 0; field < values.Length; field++)
            {
                Assert.Equal(values[field], row[field]!.Value.ToArray());
            }
            Assert.Throws<ArgumentOutOfRangeException>(() => row[values.Length]);
            row.Dispose();
            var following = pool.Rent(Message(Int64(iteration)), null, null);
            row.Dispose();
            Assert.Throws<ObjectDisposedException>(() => row[0]);
            Assert.Equal(Int64(iteration), following[0]!.Value.ToArray());
            following.Dispose();
        }
    }

    [Fact]
    public async Task SmallLargeOwnedAndSmallRowsPreserveNullEmptyAndBudgetOwnership()
    {
        await using var wire = new ScriptedSession();
        await using var batch = wire.Session.CreateBatch(Token);
        var pool = new RowStoragePool(singleRenter: true);
        var budget = new RowBufferBudget(512);
        var small = Message(null, [], Int64(7));
        var large = Message(Enumerable.Range(0, 123).Select(i => (byte)i).ToArray());
        Assert.Equal(129, large.Payload.Length);
        for (var iteration = 0; iteration < 32; iteration++)
        {
            Assert.True(await budget.ReserveAsync(small.Payload.Length, batch, Token));
            var first = pool.Rent(small, null, budget);
            Assert.Null(first[0]);
            Assert.True(first[1]!.Value.IsEmpty);
            Assert.Equal(Int64(7), first[2]!.Value.ToArray());
            first.Dispose();
            Assert.Equal(0, budget.Used);

            Assert.True(await budget.ReserveAsync(large.Payload.Length, batch, Token));
            var second = pool.Rent(large, null, budget);
            first.Dispose();
            Assert.Equal(large.Payload.Length, budget.Used);
            Assert.Equal(large.Payload.Slice(6).ToArray(), second[0]!.Value.ToArray());
            second.Dispose();
            Assert.Equal(0, budget.Used);

            var owner = new CountingOwner(small.Payload.ToArray());
            Assert.True(await budget.ReserveAsync(owner.Memory.Length, batch, Token));
            var owned = pool.Rent(Message(new ReadOnlySequence<byte>(owner.Memory), 3), owner, budget);
            Assert.Null(owned[0]);
            Assert.Equal(Int64(7), owned[2]!.Value.ToArray());
            Assert.Equal(0, owner.Disposals);
            owned.Dispose();
            Assert.Equal(1, owner.Disposals);
            Assert.Equal(0, budget.Used);

            Assert.True(await budget.ReserveAsync(small.Payload.Length, batch, Token));
            var final = pool.Rent(small, null, budget);
            second.Dispose();
            owned.Dispose();
            Assert.Throws<ObjectDisposedException>(() => owned[2]);
            Assert.Null(final[0]);
            Assert.True(final[1]!.Value.IsEmpty);
            Assert.Equal(Int64(7), final[2]!.Value.ToArray());
            Assert.Equal(small.Payload.Length, budget.Used);
            final.Dispose();
            Assert.Equal(0, budget.Used);
        }
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task MalformedSmallBackingLeavesReservationAndSuppliedFrameWithCaller(bool segmentedOwner)
    {
        await using var wire = new ScriptedSession();
        await using var batch = wire.Session.CreateBatch(Token);
        var pool = new RowStoragePool(singleRenter: true);
        var budget = new RowBufferBudget(256);
        var payload = Row(Int64(7), null, new byte[106]).AsSpan(5).ToArray();
        Assert.Equal(128, payload.Length);
        // The last field claims 107 bytes while the complete frame contains only 106.
        payload[21] = 107;
        var owner = new CountingOwner(payload);
        var message = segmentedOwner
            ? Message(Segments(owner.Memory[..64], owner.Memory[64..]), 3)
            : Message(new ReadOnlySequence<byte>(payload), 3);
        Assert.True(await budget.ReserveAsync(128, batch, Token));
        Assert.Throws<InvalidDataException>(() => pool.Rent(message, segmentedOwner ? owner : null, budget));
        Assert.Equal(0, owner.Disposals);
        Assert.Equal(128, budget.Used);
        owner.Dispose();
        budget.Release(128);
        Assert.Equal(0, budget.Used);

        var good = Message(null, [], Int64(99));
        Assert.True(await budget.ReserveAsync(good.Payload.Length, batch, Token));
        var next = pool.Rent(good, null, budget);
        Assert.Null(next[0]);
        Assert.True(next[1]!.Value.IsEmpty);
        Assert.Equal(Int64(99), next[2]!.Value.ToArray());
        next.Dispose();
        Assert.Equal(0, budget.Used);
    }

    [Fact]
    public void CachedSmallBackingsDoNotRetainOriginalCallerArraysOrFrameOwners()
    {
        var pool = new RowStoragePool(singleRenter: true);
        var weak = FillAndRelease(pool);
        Assert.Equal(64, pool.RetainedStorageCount);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        foreach (var reference in weak)
        {
            Assert.False(reference.IsAlive);
        }
        GC.KeepAlive(pool);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] FillAndRelease(RowStoragePool pool)
    {
        var rows = new OwnedRow[64];
        var weak = new List<WeakReference>();
        for (var i = 0; i < rows.Length; i++)
        {
            var bytes = Row(Int64(i), null, []).AsSpan(5).ToArray();
            weak.Add(new WeakReference(bytes));
            if (i % 2 == 0)
            {
                rows[i] = pool.Rent(Message(new ReadOnlySequence<byte>(bytes), 3), null, null);
            }
            else
            {
                var owner = new CountingOwner(bytes);
                weak.Add(new WeakReference(owner));
                rows[i] = pool.Rent(Message(new ReadOnlySequence<byte>(owner.Memory), 3), owner, null);
            }
        }
        foreach (var row in rows)
        {
            row.Dispose();
        }
        var next = pool.Rent(Message(Int64(42)), null, null);
        next.Dispose();
        return weak.ToArray();
    }

    [Theory, InlineData(14), InlineData(128)]
    public void WarmSmallRowCopiesDoNotAllocatePerRental(int payloadBytes)
    {
        var pool = new RowStoragePool(singleRenter: true);
        var message = Message(new byte[payloadBytes - 6]);
        for (var i = 0; i < 64; i++)
        {
            var row = pool.Rent(message, null, null);
            _ = row[0];
            row.Dispose();
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            var row = pool.Rent(message, null, null);
            _ = row[0];
            row.Dispose();
        }
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    private static ReadOnlySequence<byte> Segments(ReadOnlyMemory<byte> first, ReadOnlyMemory<byte> second)
    {
        var head = new Segment(first);
        var tail = head.Append(second);
        return new ReadOnlySequence<byte>(head, 0, tail, tail.Memory.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        internal Segment(ReadOnlyMemory<byte> memory)
        {
            Memory = memory;
        }
        internal Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
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
