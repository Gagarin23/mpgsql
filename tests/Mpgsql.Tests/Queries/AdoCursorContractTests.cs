using System.Reflection;
using Mpgsql.Internal;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class AdoCursorContractTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    [Fact]
    public void ProviderStoresAConcreteCursorWithoutARawReaderOrBatchAdapter()
    {
        var field = typeof(MpgsqlDataReader).GetField("_reader", InstanceFields)!;
        Assert.Equal(typeof(AdoCursor), field.FieldType);
        Assert.True(typeof(AdoCursor).IsSealed);
        Assert.False(typeof(AdoCursor).IsVisible);
        Assert.Same(typeof(MpgsqlMessageSession).Assembly, typeof(AdoCursor).Assembly);
        Assert.DoesNotContain(typeof(AdoCursor).GetFields(InstanceFields), member =>
            member.FieldType == typeof(MpgsqlResultReader) || member.FieldType == typeof(MpgsqlQueryBatch));
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task SeparateOperationsKeepClosedReaderMetadataAndMovementIsolated(bool batchPath)
    {
        await using var wire = new ScriptedSession();
        var cancelCalls = 0;
        await using var source = new MpgsqlDataSource(_ => ValueTask.FromResult(wire.Session), (_, _) =>
        {
            Interlocked.Increment(ref cancelCalls);
            return ValueTask.CompletedTask;
        });
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand("select cursor metadata");
        await using var batch = connection.CreateBatch();
        batch.BatchCommands.Add(new MpgsqlBatchCommand("select cursor metadata"));

        var firstOpening = batchPath ? batch.ExecuteReaderAsync(Token) : command.ExecuteReaderAsync(Token);
        await Sync(wire);
        var firstWriting = wire.WriteAsync(Join(Query(7), Ready()), 1);
        await using var previous = await firstOpening.WaitAsync(TestTimeout, Token);
        var firstCursor = GetCursor(previous);
        var originalColumns = previous.Columns;
        var originalColumn = originalColumns.Span[0];
        var originalName = previous.GetName(0);
        var originalTypeName = previous.GetDataTypeName(0);
        Assert.True(await previous.ReadAsync(Token));
        Assert.Equal(7L, previous.GetInt64(0));
        Assert.False(await previous.ReadAsync(Token));
        Assert.False(await previous.NextResultAsync(Token));
        await firstWriting.WaitAsync(TestTimeout, Token);
        var originalCommandTag = previous.CommandTag;
        var originalAffectedRows = previous.RecordsAffected64;
        await previous.CloseAsync().WaitAsync(TestTimeout, Token);
        Assert.True(previous.IsClosed);
        Assert.True(wire.Session.IsIdleAndHealthy);

        var nextOpening = batchPath ? batch.ExecuteReaderAsync(Token) : command.ExecuteReaderAsync(Token);
        await Sync(wire);
        Assert.False(nextOpening.IsCompleted);
        await previous.DisposeAsync();
        await previous.CloseAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => previous.ReadAsync(Token));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => previous.NextResultAsync(Token));
        Assert.Throws<ObjectDisposedException>(() => previous.GetRawValue(0));
        Assert.Throws<ObjectDisposedException>(() => previous.GetValue(0));
        Assert.Throws<ObjectDisposedException>(() => previous.GetFieldValue<long>(0));
        Assert.Equal(0, Volatile.Read(ref cancelCalls));
        Assert.False(nextOpening.IsCompleted);
        AssertPreviousMetadata();

        var nextWriting = wire.WriteAsync(Join(Begin(25, 20),
            Row("later"u8.ToArray(), Int64(9)), Row("tail"u8.ToArray(), Int64(10)), Command("SELECT 2"), Ready()), 3);
        await using var reader = await nextOpening.WaitAsync(TestTimeout, Token);
        Assert.NotSame(previous, reader);
        Assert.NotSame(firstCursor, GetCursor(reader));
        Assert.Equal(2, reader.FieldCount);
        Assert.Equal(25U, reader.Columns.Span[0].DataTypeOid);
        AssertPreviousMetadata();
        Assert.True(await reader.ReadAsync(Token));
        Assert.Equal("later", reader.GetString(0));
        Assert.Equal(9L, reader.GetInt64(1));
        Assert.True(await reader.ReadAsync(Token));
        Assert.Equal("tail", reader.GetString(0));
        Assert.Equal(10L, reader.GetInt64(1));
        Assert.False(await reader.ReadAsync(Token));
        Assert.False(await reader.NextResultAsync(Token));
        await nextWriting.WaitAsync(TestTimeout, Token);
        AssertPreviousMetadata();
        Assert.Equal(0, Volatile.Read(ref cancelCalls));
        Assert.True(wire.Session.IsIdleAndHealthy);

        void AssertPreviousMetadata()
        {
            Assert.Equal(1, previous.FieldCount);
            Assert.Equal(originalColumn, originalColumns.Span[0]);
            Assert.Equal(originalColumn, previous.Columns.Span[0]);
            Assert.Equal(originalName, previous.GetName(0));
            Assert.Equal(originalTypeName, previous.GetDataTypeName(0));
            Assert.Equal(typeof(long), previous.GetFieldType(0));
            Assert.Equal(originalCommandTag, previous.CommandTag);
            Assert.Equal(originalAffectedRows, previous.RecordsAffected64);
        }
    }

    private static AdoCursor GetCursor(MpgsqlDataReader reader)
    {
        var cursor = Assert.IsType<AdoCursor>(typeof(MpgsqlDataReader).GetField("_reader", InstanceFields)!.GetValue(reader));
        Assert.DoesNotContain(typeof(AdoCursor).GetFields(InstanceFields), field =>
            field.GetValue(cursor) is MpgsqlResultReader or MpgsqlQueryBatch);
        return cursor;
    }

    private static async Task Sync(ScriptedSession wire)
    {
        var bytes = new List<byte>();
        do { bytes.AddRange(await wire.ReadOutputAsync()); }
        while (!Tags([.. bytes]).Contains('S'));
    }
}
