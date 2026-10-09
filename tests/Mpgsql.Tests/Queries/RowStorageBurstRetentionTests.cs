using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Mpgsql.Internal;
using Mpgsql.Protocol;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class RowStorageBurstRetentionTests
{
    [Theory, InlineData(false), InlineData(true)]
    public void WarmFourThousandRowBurstReusesStorageAndCopiedBackingWithoutAllocations(bool singleRenter)
    {
        // Hold the whole result before consuming any rows, as a receiver burst can do.
        // The assertion concerns the workload's reuse, without reading the pool capacity.
        const int resultRows = 4096;
        var pool = new RowStoragePool(singleRenter);
        var rows = new OwnedRow[resultRows];
        var stale = new OwnedRow[resultRows];
        var first = Message(7);
        var next = Message(99);

        RentBurst(pool, first, rows);
        rows.CopyTo(stale, 0);
        Assert.Equal(7L * resultRows, ConsumeAndRelease(rows));

        RentBurst(pool, next, rows);
        // Copied old handles must not release any storage from the following whole burst.
        foreach (var old in stale)
        {
            old.Dispose();
        }
        Assert.Throws<ObjectDisposedException>(() => stale[0][0]);
        Assert.Throws<ObjectDisposedException>(() => stale[^1][0]);
        Assert.Equal(99L * resultRows, ConsumeAndRelease(rows));

        var before = GC.GetAllocatedBytesForCurrentThread();
        long checksum = 0;
        for (var repetition = 0; repetition < 4; repetition++)
        {
            RentBurst(pool, next, rows);
            checksum += ConsumeAndRelease(rows);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(99L * resultRows * 4, checksum);
        Assert.Equal(0, allocated);
        GC.KeepAlive(pool);
        GC.KeepAlive(stale);
    }

    private static BackendMessage Message(long value)
        => new BackendMessage((byte)'D', BackendMessageKind.DataRow,
            new ReadOnlySequence<byte>(Row(Int64(value)).AsMemory(5)), 1);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RentBurst(RowStoragePool pool, BackendMessage message, OwnedRow[] rows)
    {
        for (var i = 0; i < rows.Length; i++)
        {
            rows[i] = pool.Rent(message, null, null);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long ConsumeAndRelease(OwnedRow[] rows)
    {
        long checksum = 0;
        try
        {
            foreach (var row in rows)
            {
                checksum += BinaryPrimitives.ReadInt64BigEndian(row[0]!.Value.FirstSpan);
            }
            return checksum;
        }
        finally
        {
            foreach (var row in rows)
            {
                row.Dispose();
            }
        }
    }
}
