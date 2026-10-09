using System.Buffers;
using System.Reflection;
using Mpgsql.Internal;
using Mpgsql.Protocol;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class AdoColumnStorageCacheTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task BatchDescriptionsChangeOidAndColumnCountAcrossNoDataWithoutStaleGetters()
    {
        await using var wire = new ScriptedSession();
        var mapper = new MpgsqlTypeMapper().Register<long>(90000, payload => payload.FirstSpan[0]);
        await using var source = Source(wire, mapper);
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var batch = connection.CreateBatch();
        foreach (var sql in new[] { "select bigint", "update no rows", "select money", "select custom", "select two bigint" })
            batch.BatchCommands.Add(new MpgsqlBatchCommand(sql));
        var opening = batch.ExecuteReaderAsync(Token);
        await Sync(wire);
        var writing = wire.WriteAsync(Join(
            Begin(20), Row(Int64(5)), Command("SELECT 1"),
            Packet('1'), Packet('2'), Packet('n'), Command("UPDATE 0"),
            Begin(790), Row(Int64(6)), Command("SELECT 1"),
            Begin(90000), Row(new byte[] { 7 }), Command("SELECT 1"),
            Begin(20, 20), Row(Int64(8), Int64(9)), Command("SELECT 1"), Ready()), 1);
        await using var reader = await opening.WaitAsync(TestTimeout, Token);
        Assert.True(await reader.ReadAsync(Token));
        Assert.Equal(5L, reader.GetFieldValue<long>(0));
        Assert.Equal(5L, reader.GetValue(0));
        Assert.True(await reader.NextResultAsync(Token));
        Assert.Equal(0, reader.FieldCount);
        Assert.False(reader.HasRows);
        Assert.False(await reader.ReadAsync(Token));
        Assert.Throws<InvalidOperationException>(() => reader.GetFieldValue<long>(0));
        Assert.True(await reader.NextResultAsync(Token));
        Assert.True(await reader.ReadAsync(Token));
        Assert.Equal(6L, reader.GetFieldValue<long>(0));
        Assert.Equal(6L, reader.GetValue(0));
        Assert.True(await reader.NextResultAsync(Token));
        Assert.True(await reader.ReadAsync(Token));
        Assert.Equal(7L, reader.GetFieldValue<long>(0));
        Assert.Throws<NotSupportedException>(() => reader.GetValue(0));
        Assert.True(await reader.NextResultAsync(Token));
        Assert.Equal(2, reader.FieldCount);
        Assert.True(await reader.ReadAsync(Token));
        Assert.Equal(8L, reader.GetFieldValue<long>(0));
        Assert.Equal(9L, reader.GetFieldValue<long>(1));
        Assert.Equal(9L, reader.GetValue(1));
        Assert.False(await reader.NextResultAsync(Token));
        await writing.WaitAsync(TestTimeout, Token);
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task ArraySliceAndMemoryManagerMetadataRetainThePublicCustomOidContract(bool memoryManager)
    {
        await using var wire = new ScriptedSession();
        var mapper = new MpgsqlTypeMapper().Register<long>(90000, payload => payload.FirstSpan[0]);
        await using var source = Source(wire, mapper);
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand("select alternate metadata storage");
        var opening = command.ExecuteReaderAsync(Token);
        await Sync(wire);
        var writing = wire.WriteAsync(Join(Begin(20), Row(new byte[] { 42 }), Command("SELECT 1"), Ready()));
        await using var reader = await opening.WaitAsync(TestTimeout, Token);
        Assert.True(await reader.ReadAsync(Token));
        var original = reader.Columns.Span[0];
        var custom = original with { DataTypeOid = 90000, DataTypeSize = -1 };
        RowField[] storage = [original, custom, original];
        using var managed = memoryManager ? new MetadataMemory([custom]) : null;
        ReadOnlyMemory<RowField> replacement = memoryManager ? managed!.Memory : storage.AsMemory(1, 1);

        // The wire decoder currently supplies owned arrays. Exercise the other
        // valid ReadOnlyMemory storage shapes at the same private assignment
        // boundary, then assert observable getters rather than cache fields.
        var cursor = (AdoCursor)typeof(MpgsqlDataReader)
            .GetField("_reader", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reader)!;
        typeof(AdoCursor).GetMethod("SetColumns", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(cursor, [replacement]);
        Assert.Equal(90000U, reader.Columns.Span[0].DataTypeOid);
        Assert.Equal(42L, reader.GetFieldValue<long>(0));
        Assert.Throws<NotSupportedException>(() => reader.GetValue(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.GetFieldValue<long>(1));
        Assert.False(await reader.NextResultAsync(Token));
        await writing.WaitAsync(TestTimeout, Token);
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    private static MpgsqlDataSource Source(ScriptedSession wire, MpgsqlTypeMapper mapper)
        => new MpgsqlDataSource(_ => ValueTask.FromResult(wire.Session), (_, _) => ValueTask.CompletedTask,
            new MpgsqlDataSourceOptions { TypeMapper = mapper });

    private static async Task Sync(ScriptedSession wire)
    {
        var bytes = new List<byte>();
        do { bytes.AddRange(await wire.ReadOutputAsync()); }
        while (!Tags([.. bytes]).Contains('S'));
    }

    private sealed class MetadataMemory(RowField[] fields) : MemoryManager<RowField>
    {
        public override Span<RowField> GetSpan() => fields;
        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }
}
