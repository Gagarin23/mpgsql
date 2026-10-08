using System.Buffers;
using System.Buffers.Binary;
using Mpgsql.Converters;
using Mpgsql.Internal;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class FramingTests
{
    [Fact]
    public async Task LargeRowsSurvivePipeAdvancesAndEarlyBuffering()
    {
        await using var wire = new ScriptedSession();
        var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await batch.SendQueryAsync("select arrays");
        await batch.SendSyncAsync();
        long[] array =
        [
            .. Enumerable.Range(0,
                8192).Select(i => (long)i)
        ];
        var payload = new byte[Int64ArrayConverter.GetByteCount(array)];
        Int64ArrayConverter.Write(array,
            payload);
        var second = new byte[payload.Length];
        long[] negative = [.. array.Select(i => -i)];
        Int64ArrayConverter.Write(negative,
            second);
        await wire.WriteAsync(Join(Begin(1016),
                Row(payload),
                Row(second),
                Command("SELECT 2"),
                Ready()),
            17);
        await batch.Completion.WaitAsync(TestTimeout,
            TestContext.Current.CancellationToken);
        await using var reader = await batch.ReadResultsAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(array,
            reader.GetInt64Array(0)!.Value.ToArray());
        var owned = reader.GetInt64Array(0)!.Value; // decoded arrays outlive row movement
        Assert.True(await reader.ReadAsync());
        Assert.Equal(negative,
            reader.GetInt64Array(0)!.Value.ToArray());
        Assert.Equal(array,
            owned.ToArray());
        Assert.False(await reader.ReadAsync());
        Assert.Equal("SELECT 2",
            reader.CommandTag);
        Assert.Throws<InvalidOperationException>(() => reader.GetRawValue(0));
        Assert.False(await reader.NextResultAsync());
        await batch.DisposeAsync();
    }

    [Fact]
    public async Task NullEmptyAndNullableArrayResultsUseDistinctRepresentations()
    {
        await using var wire = new ScriptedSession();
        var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await batch.SendQueryAsync("select typed values");
        await batch.SendSyncAsync();
        var empty = new byte[Int64ArrayConverter.GetByteCount(ReadOnlyMemory<long>.Empty)];
        Int64ArrayConverter.Write(ReadOnlyMemory<long>.Empty,
            empty);
        long?[] values = [1, null, long.MinValue];
        var nullable = new byte[NullableInt64ArrayConverter.GetByteCount(values)];
        NullableInt64ArrayConverter.Write(values,
            nullable);
        await wire.WriteAsync(Join(Begin(20,
                    1016,
                    1016,
                    1016),
                Row(null,
                    null,
                    empty,
                    nullable),
                Command(),
                Ready()),
            1);
        await using var reader = await batch.ReadResultsAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Null(reader.GetInt64(0));
        Assert.Null(reader.GetInt64Array(1));
        Assert.Empty(reader.GetInt64Array(2)!.Value.ToArray());
        Assert.Equal(values,
            reader.GetNullableInt64Array(3)!.Value.ToArray());
        Assert.Throws<InvalidCastException>(() => reader.GetInt64(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.GetRawValue(4));
        Assert.False(await reader.NextResultAsync());
        await batch.DisposeAsync();
    }

    [Theory, InlineData(3), InlineData(64 * 1024 * 1024 + 1)]
    public async Task InvalidFrameLengthFaultsTransport(int length)
    {
        await using var wire = new ScriptedSession();
        var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await batch.SendQueryAsync("select 1");
        byte[] header = [(byte)'D', 0, 0, 0, 0];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(1),
            length);
        await wire.WriteAsync(header,
            1);
        await Assert.ThrowsAsync<InvalidDataException>(() => wire.Session.Completion.WaitAsync(TestTimeout,
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => batch.ReadResultsAsync().AsTask());
    }

    [Fact]
    public async Task TruncatedAndCopyResponsesFaultTransport()
    {
        await using (var wire = new ScriptedSession())
        {
            var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
            await batch.SendQueryAsync("select 1");
            await wire.WriteAsync([(byte)'D', 0, 0]);
            await wire.Incoming.Writer.CompleteAsync();
            await Assert.ThrowsAsync<EndOfStreamException>(() => wire.Session.Completion.WaitAsync(TestTimeout,
                TestContext.Current.CancellationToken));
        }
        await using (var wire = new ScriptedSession())
        {
            var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
            await batch.SendQueryAsync("copy table to stdout");
            await wire.WriteAsync(Join(Packet('1'),
                Packet('2'),
                Packet('n'),
                Packet('H',
                    1,
                    0,
                    1,
                    0,
                    1)));
            await Assert.ThrowsAsync<NotSupportedException>(() => wire.Session.Completion.WaitAsync(TestTimeout,
                TestContext.Current.CancellationToken));
        }
    }

    [Theory, InlineData(false), InlineData(true)]
    public void MalformedDiscardedRowStillChecksLengthPrefixes(bool fragmented)
    {
        var frame = new BackendFrameBuffer();
        using (frame)
        {
            var row = Packet('D',
                0,
                1,
                255,
                255,
                255,
                254); // length -2 is invalid
            Assert.Throws<InvalidDataException>(() =>
            {
                if (fragmented)
                {
                    foreach (var value in row)
                    {
                        var input = new ReadOnlySequence<byte>([value]);
                        frame.TryRead(ref input,
                            true,
                            out _,
                            out var owner,
                            out _);
                        owner?.Dispose();
                    }
                }
                else
                {
                    var input = new ReadOnlySequence<byte>(row);
                    frame.TryRead(ref input,
                        true,
                        out _,
                        out var owner,
                        out _);
                    owner?.Dispose();
                }
            });
        }
    }

    [Fact]
    public void CancellationDuringPartialFrameStopsFurtherCopying()
    {
        using var frames = new BackendFrameBuffer();
        var row = Row(new byte[128 * 1024]);
        var first = new ReadOnlySequence<byte>(row.AsMemory(0,
            17));
        Assert.False(frames.TryRead(ref first,
            false,
            out _,
            out _,
            out _));
        Assert.True(first.IsEmpty);
        var copied = frames.CopiedRowBytes;
        var rest = new ReadOnlySequence<byte>(row.AsMemory(17));
        Assert.True(frames.TryRead(ref rest,
            true,
            out _,
            out var owner,
            out var columns));
        Assert.Null(owner);
        Assert.Equal(1,
            columns);
        Assert.True(rest.IsEmpty);
        Assert.Equal(copied,
            frames.CopiedRowBytes);
        Assert.False(frames.HasPartialFrame);
    }

    [Fact]
    public async Task ConcurrentReaderMovementIsRejected()
    {
        await using var wire = new ScriptedSession();
        var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await batch.SendQueryAsync("select 1::bigint");
        await wire.WriteAsync(Begin(20));
        var reader = await batch.ReadResultsAsync();
        var waiting = reader.ReadAsync().AsTask();
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.NextResultAsync().AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.DisposeAsync().AsTask());
        await wire.WriteAsync(Join(Row(Int64(1)),
            Command()));
        Assert.True(await waiting.WaitAsync(TestTimeout,
            TestContext.Current.CancellationToken));
        await batch.SendSyncAsync();
        await wire.WriteAsync(Ready());
        Assert.False(await reader.NextResultAsync());
        await reader.DisposeAsync();
        await batch.DisposeAsync();
    }
}