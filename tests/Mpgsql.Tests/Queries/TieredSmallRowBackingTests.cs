using System.Buffers;
using System.Runtime.CompilerServices;
using Mpgsql.Internal;
using Mpgsql.Protocol;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class TieredSmallRowBackingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory, InlineData(false), InlineData(true)]
    public async Task GrowingThenSmallRowsPreserveEveryLeaseAndReleaseSourceAndBudget(bool singleRenter)
    {
        await using var wire = new ScriptedSession();
        await using var batch = wire.Session.CreateBatch(Token);
        var pool = new RowStoragePool(singleRenter);
        var budget = new RowBufferBudget(256);
        byte[]?[][] shapes =
        [
            [Int64(7)],
            [null, [], Enumerable.Range(0, 13).Select(i => (byte)(i + 17)).ToArray()],
            [Enumerable.Range(0, 46).Select(i => (byte)(255 - i)).ToArray()],
            [new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, null,
                Enumerable.Range(0, 70).Select(i => (byte)(i + 61)).ToArray(), []],
            [null],
            [Int64(long.MinValue)],
            [[], null, []]
        ];
        var oldLeases = new List<OwnedRow>();
        var sources = new List<WeakReference>();
        foreach (var values in shapes)
        {
            var row = RentAndForgetSource(pool, budget, batch, values, out var source, out var reservedBytes);
            sources.Add(source);
            try
            {
                foreach (var stale in oldLeases)
                {
                    stale.Dispose();
                    Assert.Throws<ObjectDisposedException>(() => stale[0]);
                }
                Assert.Equal(reservedBytes, budget.Used);
                for (var ordinal = 0; ordinal < values.Length; ordinal++)
                {
                    if (values[ordinal] is { } expected)
                    {
                        Assert.Equal(expected, row[ordinal]!.Value.ToArray());
                    }
                    else
                    {
                        Assert.Null(row[ordinal]);
                    }
                }
                Assert.Throws<ArgumentOutOfRangeException>(() => row[values.Length]);
            }
            finally { row.Dispose(); }
            Assert.Equal(0, budget.Used);
            oldLeases.Add(row);
        }
        Assert.Equal(1, pool.RetainedStorageCount);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        foreach (var source in sources)
        {
            Assert.False(source.IsAlive);
        }
        foreach (var stale in oldLeases)
        {
            stale.Dispose();
            Assert.Throws<ObjectDisposedException>(() => stale[0]);
        }
        Assert.Equal(0, budget.Used);
        GC.KeepAlive(pool);
        GC.KeepAlive(oldLeases);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static OwnedRow RentAndForgetSource(
        RowStoragePool pool, RowBufferBudget budget,
        MpgsqlQueryBatch batch, byte[]?[] values,
        out WeakReference source, out long reservedBytes
    )
    {
        var original = Row(values);
        source = new WeakReference(original);
        var payload = new ReadOnlySequence<byte>(original.AsMemory(5));
        reservedBytes = payload.Length;
        var reservation = budget.ReserveAsync(reservedBytes, batch, Token);
        Assert.True(reservation.IsCompletedSuccessfully);
        Assert.True(reservation.Result); // This test has no outstanding reservation or wait.
        return pool.Rent(new BackendMessage((byte)'D', BackendMessageKind.DataRow, payload, values.Length), null, budget);
    }
}
