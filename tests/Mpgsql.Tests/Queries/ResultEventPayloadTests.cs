using System.Buffers;
using Mpgsql.Internal;
using Mpgsql.Protocol;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class ResultEventPayloadTests
{
    [Theory, InlineData(0, 0), InlineData(1, 2), InlineData(3, 0)]
    public async Task DescriptionKeepsTheExactMemorySliceThroughTheBuffer(int start, int count)
    {
        RowField[] fields =
        [
            new RowField("one", 0, 0, 20, 8, -1, FormatCode.Binary),
            new RowField("two", 0, 0, 20, 8, -1, FormatCode.Binary),
            new RowField("three", 0, 0, 20, 8, -1, FormatCode.Binary)
        ];
        var columns = fields.AsMemory(start, count);
        var buffer = new ResultEventBuffer();
        Assert.True(buffer.TryWrite(new ResultEvent(17, columns, IsRowSet: true)));
        buffer.Complete();
        Assert.True(buffer.TryRead(out var result));
        Assert.Equal(17, result.QueryIndex);
        Assert.Equal((ReadOnlyMemory<RowField>)columns, result.Columns);
        Assert.True(result.IsRowSet);
        Assert.False(result.IsEnd);
        Assert.Null(result.Row);
        Assert.Null(result.CommandTag);
        Assert.False(await buffer.WaitToReadAsync());
    }

    [Fact]
    public void NoDataAndEndMarkersKeepTheirDistinctMeanings()
    {
        var buffer = new ResultEventBuffer();
        Assert.True(buffer.TryWrite(new ResultEvent(2, default)));
        Assert.True(buffer.TryWrite(new ResultEvent(2, default, CommandTag: "UPDATE 5", IsEnd: true)));
        Assert.True(buffer.TryWrite(new ResultEvent(3, default, IsEnd: true)));
        buffer.Complete();
        Assert.True(buffer.TryRead(out var noData));
        Assert.False(noData.IsRowSet);
        Assert.False(noData.IsEnd);
        Assert.True(noData.Columns.IsEmpty);
        Assert.Equal(default(ReadOnlyMemory<RowField>), noData.Columns);
        Assert.Null(noData.Row);
        Assert.Null(noData.CommandTag);
        Assert.True(buffer.TryRead(out var end));
        Assert.True(end.IsEnd);
        Assert.Equal("UPDATE 5", end.CommandTag);
        Assert.Null(end.Row);
        Assert.True(end.Columns.IsEmpty);
        Assert.True(buffer.TryRead(out var emptyEnd));
        Assert.True(emptyEnd.IsEnd);
        Assert.Null(emptyEnd.CommandTag);
        Assert.False(buffer.TryRead(out _));
    }

    [Fact]
    public void BufferedCopiesKeepTheOriginalLeaseGenerationAfterStorageReuse()
    {
        var pool = new RowStoragePool();
        var message = new BackendMessage((byte)'D', BackendMessageKind.DataRow,
            new ReadOnlySequence<byte>(Row(Int64(55)).AsMemory(5)), 1);
        var original = pool.Rent(message, null, null);
        var buffer = new ResultEventBuffer();
        Assert.True(buffer.TryWrite(new ResultEvent(7, default, original)));
        Assert.True(buffer.TryRead(out var item));
        var stale = item.Row!.Value;
        original.Dispose();
        using var next = pool.Rent(message, null, null);
        stale.Dispose();
        Assert.Throws<ObjectDisposedException>(() => stale[0]);
        Assert.Equal(Int64(55), next[0]!.Value.ToArray());
        Assert.Equal(7, item.QueryIndex);
        buffer.Complete();
    }

    [Fact]
    public void DefaultEventDoesNotOwnRowsOrClaimAResultSet()
    {
        ResultEvent item = default;
        Assert.False(item.IsRowSet);
        Assert.False(item.IsEnd);
        Assert.True(item.Columns.IsEmpty);
        Assert.Null(item.Row);
        Assert.Null(item.CommandTag);
    }
}