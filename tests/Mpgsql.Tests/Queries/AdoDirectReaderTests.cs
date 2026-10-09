using System.Buffers;
using Mpgsql.Protocol;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class AdoDirectReaderTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static MpgsqlDataSource Source(ScriptedSession wire, long rowBytes)
        => new MpgsqlDataSource(
            _ => ValueTask.FromResult(wire.Session), (_, _) => ValueTask.CompletedTask,
            new MpgsqlDataSourceOptions { MaxBufferedRowBytesPerConnection = rowBytes });

    [Theory, InlineData(false), InlineData(true)]
    public async Task CompleteBorrowedRowKeepsProducerPausedUntilMovementOrAsyncClose(bool close)
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire, 14);
        await using var connection = await source.OpenConnectionAsync(Token);
        Assert.True(wire.Session.IsAdoSession);
        await using var command = connection.CreateCommand("select held row");
        var copied = wire.Session.CopiedRowBytes;
        var opening = command.ExecuteReaderAsync(Token);
        await Sync(wire);
        var initialWrite = wire.WriteAsync(Join(Begin(20), Row(Int64(42))));
        await using var reader = await opening.WaitAsync(TestTimeout, Token);
        Assert.True(reader.HasRows);
        // Incoming retains its pause=32/resume=16 thresholds. ExecuteReader's
        // HasRows prefetch has borrowed the row without releasing the ReadResult.
        Assert.False(initialWrite.IsCompleted);
        Assert.True(await reader.ReadAsync(Token));
        Assert.Equal(42L, reader.GetInt64(0));
        Assert.Equal(Int64(42), reader.GetRawValue(0)!.Value.ToArray());
        Assert.False(initialWrite.IsCompleted);
        Assert.Equal(copied, wire.Session.CopiedRowBytes);
        Assert.Equal(0, wire.Session.BufferedRowBytes);

        if (close)
        {
            var closing = reader.CloseAsync();
            await initialWrite.WaitAsync(TestTimeout, Token);
            Assert.False(closing.IsCompleted);
            await wire.WriteAsync(Join(Command(), Ready()));
            await closing.WaitAsync(TestTimeout, Token);
            Assert.True(reader.IsClosed);
        }
        else
        {
            var reading = reader.ReadAsync(Token);
            await initialWrite.WaitAsync(TestTimeout, Token);
            Assert.False(reading.IsCompleted);
            await wire.WriteAsync(Join(Command(), Ready()));
            Assert.False(await reading.WaitAsync(TestTimeout, Token));
            Assert.Throws<InvalidOperationException>(() => reader.GetRawValue(0));
            Assert.False(await reader.NextResultAsync(Token).WaitAsync(TestTimeout, Token));
        }
        Assert.Equal(copied, wire.Session.CopiedRowBytes);
        Assert.True(wire.Session.IsIdleAndHealthy);
        command.CommandText = "reusable after releasing the borrowed row";
    }

    [Fact]
    public async Task FragmentedBorrowedRowsPreserveNullEmptyRawUtf8AndFollowingExecutionMetadata()
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire, 14);
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand("select fragmented values");
        ReadOnlyMemory<RowField> previousColumns = default;
        string? previousTag = null;
        var text = "Я😀"u8.ToArray();
        for (var iteration = 0; iteration < 2; iteration++)
        {
            var value = 11L + iteration;
            var rowFrame = Row(Int64(value), text, [], null);
            Assert.True(rowFrame.Length > 32);
            var copied = wire.Session.CopiedRowBytes;
            var opening = command.ExecuteReaderAsync(Token);
            await Sync(wire);
            // One-byte fragments and a frame above the tiny pipe threshold force
            // incremental assembly, including split length prefixes and UTF-8 scalars.
            var initialWrite = wire.WriteAsync(Join(Begin(20, 25, 17, 25), rowFrame), 1);
            await using var reader = await opening.WaitAsync(TestTimeout, Token);
            Assert.True(reader.HasRows);
            Assert.Equal(4, reader.FieldCount);
            Assert.True(await reader.ReadAsync(Token));
            Assert.Equal(value, reader.GetInt64(0));
            Assert.Equal("Я😀", reader.GetString(1));
            Assert.Equal(text, reader.GetRawValue(1)!.Value.ToArray());
            Assert.False(reader.IsDBNull(2));
            Assert.True(reader.GetRawValue(2)!.Value.IsEmpty);
            Assert.True(reader.IsDBNull(3));
            Assert.Null(reader.GetRawValue(3));
            // Assembly owns this single necessary copy; there is no second row copy.
            Assert.Equal(copied + rowFrame.Length, wire.Session.CopiedRowBytes);
            if (iteration == 0)
            {
                previousColumns = reader.Columns;
            }
            else
            {
                Assert.True(previousColumns.Equals(reader.Columns));
                Assert.Equal(20u, previousColumns.Span[0].DataTypeOid);
            }

            var ending = reader.ReadAsync(Token);
            await initialWrite.WaitAsync(TestTimeout, Token);
            var completionWrite = wire.WriteAsync(Join(Command(), Ready()), 1);
            Assert.False(await ending.WaitAsync(TestTimeout, Token));
            if (iteration == 0)
            {
                previousTag = reader.CommandTag;
            }
            else
            {
                Assert.Same(previousTag, reader.CommandTag);
            }
            Assert.False(await reader.NextResultAsync(Token).WaitAsync(TestTimeout, Token));
            await completionWrite.WaitAsync(TestTimeout, Token);
            Assert.True(wire.Session.IsIdleAndHealthy);
            Assert.Equal(0, wire.Session.BufferedRowBytes);
        }
    }

    private static async Task<byte[]> Sync(ScriptedSession wire)
    {
        var bytes = new List<byte>();
        do { bytes.AddRange(await wire.ReadOutputAsync()); }
        while (!Tags([.. bytes]).Contains('S'));
        return [.. bytes];
    }
}
