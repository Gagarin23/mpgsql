using System.Buffers;
using System.Buffers.Binary;
using System.Data.Common;
using Mpgsql.Internal;
using Mpgsql.Protocol;
using Mpgsql.Tests.Protocol;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class AdoBigintSpanGetterTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory, InlineData(1), InlineData(17), InlineData(int.MaxValue)]
    public async Task BigintGettersPreserveExtremaNullPositionAndOrdinalContracts(int fragment)
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire);
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand("select bigint values");
        var opening = command.ExecuteReaderAsync(Token);
        await Sync(wire);
        long[] values = [long.MinValue, long.MaxValue, 0, -1];
        var writing = wire.WriteAsync(Join(Begin(20),
            Join(values.Select(value => Row(Int64(value))).ToArray()),
            Row((byte[]?)null), Command("SELECT 5"), Ready()), fragment);
        await using DbDataReader reader = await opening.WaitAsync(TestTimeout, Token);
        Assert.Throws<InvalidOperationException>(() => reader.GetFieldValue<long>(0));
        foreach (var value in values)
        {
            Assert.True(await reader.ReadAsync(Token));
            Assert.Equal(value, reader.GetFieldValue<long>(0));
            Assert.Equal(value, reader.GetInt64(0));
            Assert.Equal(value, reader.GetFieldValue<long?>(0));
            Assert.False(reader.IsDBNull(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => reader.GetFieldValue<long>(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => reader.GetFieldValue<long>(1));
            Assert.Throws<InvalidCastException>(() => reader.GetFieldValue<string>(0));
            Assert.Equal(Int64(value), ((MpgsqlDataReader)reader).GetRawValue(0)!.Value.ToArray());
        }
        Assert.True(await reader.ReadAsync(Token));
        Assert.True(reader.IsDBNull(0));
        Assert.Null(reader.GetFieldValue<long?>(0));
        Assert.Same(DBNull.Value, reader.GetFieldValue<object>(0));
        var error = Assert.Throws<InvalidCastException>(() => reader.GetFieldValue<long>(0));
        Assert.Equal("The value is SQL NULL.", error.Message);
        Assert.Throws<InvalidCastException>(() => reader.GetInt64(0));
        Assert.False(await reader.ReadAsync(Token));
        Assert.Throws<InvalidOperationException>(() => reader.GetFieldValue<long>(0));
        Assert.False(await reader.NextResultAsync(Token));
        await writing.WaitAsync(TestTimeout, Token);
        await reader.CloseAsync();
        Assert.Throws<ObjectDisposedException>(() => reader.GetFieldValue<long>(0));
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Theory]
    [InlineData(20, 0), InlineData(20, 7), InlineData(20, 9)]
    [InlineData(790, 0), InlineData(790, 7), InlineData(790, 9)]
    public async Task MalformedBigintAndMoneyPayloadsRemainReadableAfterGetterErrors(int oid, int size)
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire);
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand("select malformed integer");
        var opening = command.ExecuteReaderAsync(Token);
        await Sync(wire);
        var malformed = new byte[size];
        var writing = wire.WriteAsync(Join(Begin((uint)oid), Row(malformed), Row(Int64(5)), Command("SELECT 2"), Ready()), 1);
        await using var reader = await opening.WaitAsync(TestTimeout, Token);
        Assert.True(await reader.ReadAsync(Token));
        Assert.Throws<InvalidDataException>(() => reader.GetFieldValue<long>(0));
        Assert.Throws<InvalidDataException>(() => reader.GetInt64(0));
        Assert.Throws<InvalidDataException>(() => reader.GetFieldValue<long?>(0));
        Assert.Equal(malformed, reader.GetRawValue(0)!.Value.ToArray());
        Assert.True(await reader.ReadAsync(Token));
        Assert.Equal(5L, reader.GetFieldValue<long>(0));
        Assert.False(await reader.NextResultAsync(Token));
        await writing.WaitAsync(TestTimeout, Token);
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Fact]
    public async Task CustomOidMoneyNullableAndBuiltinMappingPrecedenceUseTheirExistingContracts()
    {
        var customCalls = 0;
        var customFailure = new InvalidOperationException("custom reader failure");
        var mapper = new MpgsqlTypeMapper()
            .Register<long>(20, _ => throw new InvalidOperationException("Built-in bigint mapping must take precedence."))
            .Register<long>(90001, payload =>
            {
                customCalls++;
                return BinaryPrimitives.ReadInt64BigEndian(payload.ToArray());
            })
            .Register<long>(90002, _ => throw customFailure);
        await using var wire = new ScriptedSession();
        await using var source = Source(wire, mapper);
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand("select bigint money custom text");
        var opening = command.ExecuteReaderAsync(Token);
        await Sync(wire);
        var writing = wire.WriteAsync(Join(Begin(20, 790, 90001, 90002, 25),
            Row(Int64(41), Int64(-42), Int64(43), Int64(44), "not a bigint"u8.ToArray()),
            Row(null, null, null, null, null), Command("SELECT 2"), Ready()));
        await using var reader = await opening.WaitAsync(TestTimeout, Token);
        Assert.True(await reader.ReadAsync(Token));
        Assert.Equal(41L, reader.GetFieldValue<long>(0));
        Assert.Equal(41L, reader.GetFieldValue<long?>(0));
        Assert.Equal(-42L, reader.GetFieldValue<long>(1));
        Assert.Equal(-42L, reader.GetInt64(1));
        Assert.Equal(-42L, reader.GetFieldValue<long?>(1));
        Assert.Equal(43L, reader.GetFieldValue<long>(2));
        Assert.Equal(1, customCalls);
        Assert.Throws<InvalidCastException>(() => reader.GetFieldValue<long?>(2));
        Assert.Same(customFailure, Assert.Throws<InvalidOperationException>(() => reader.GetFieldValue<long>(3)));
        Assert.Throws<InvalidCastException>(() => reader.GetFieldValue<long>(4));
        Assert.True(await reader.ReadAsync(Token));
        Assert.Null(reader.GetFieldValue<long?>(0));
        Assert.Null(reader.GetFieldValue<long?>(1));
        for (var ordinal = 0; ordinal < 4; ordinal++)
        {
            Assert.Throws<InvalidCastException>(() => reader.GetFieldValue<long>(ordinal));
        }
        Assert.Throws<InvalidCastException>(() => reader.GetFieldValue<long>(4));
        Assert.Equal(1, customCalls); // SQL NULL does not invoke custom readers.
        Assert.False(await reader.NextResultAsync(Token));
        await writing.WaitAsync(TestTimeout, Token);
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Fact]
    public async Task OwnerDisposalRejectsTypedReadsEvenBeforeTheUpperReaderIsExplicitlyClosed()
    {
        await using var wire = new ScriptedSession();
        await using var source = Source(wire);
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand("select owned bigint");
        var opening = command.ExecuteReaderAsync(Token);
        await Sync(wire);
        var writing = wire.WriteAsync(Join(Query(42), Ready()));
        await using var reader = await opening.WaitAsync(TestTimeout, Token);
        Assert.True(await reader.ReadAsync(Token));
        Assert.Equal(42L, reader.GetFieldValue<long>(0));
        await command.DisposeAsync();
        await writing.WaitAsync(TestTimeout, Token);
        Assert.Throws<ObjectDisposedException>(() => reader.GetFieldValue<long>(0));
        Assert.Throws<ObjectDisposedException>(() => reader.GetFieldValue<long?>(0));
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Fact]
    public void SpanLookupPreservesBorrowingNullEmptyAndSegmentedFallbackWithoutStaleState()
    {
        var payload = Row(Int64(7), [], null).AsSpan(5).ToArray();
        var row = new BorrowedRow();
        row.Initialize(payload.AsMemory(), 3);
        Assert.True(row.TryGetContiguousValue(0, out var bigint, out var isNull));
        Assert.False(isNull);
        Assert.Equal(Int64(7), bigint.ToArray());
        payload[6] = 0x55;
        Assert.Equal((byte)0x55, bigint[0]);
        Assert.True(row.TryGetContiguousValue(1, out var empty, out isNull));
        Assert.False(isNull);
        Assert.True(empty.IsEmpty);
        Assert.True(row.TryGetContiguousValue(2, out var nil, out isNull));
        Assert.True(isNull);
        Assert.True(nil.IsEmpty);
        Assert.Throws<ArgumentOutOfRangeException>(() => row.TryGetContiguousValue(-1, out _, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => row.TryGetContiguousValue(3, out _, out _));
        row.Initialize(new BackendMessage((byte)'D', BackendMessageKind.DataRow, TestWire.ByteSegments(payload), 3));
        Assert.False(row.TryGetContiguousValue(0, out _, out _));
        Assert.Equal(payload.AsSpan(6, 8).ToArray(), row.GetValue(0)!.Value.ToArray());
        row.Reset();
        Assert.Throws<InvalidOperationException>(() => row.TryGetContiguousValue(0, out _, out _));
        foreach (var count in new[] { 8, 9 })
        {
            row.Initialize(Row(Enumerable.Range(0, count).Select(i => Int64(i)).ToArray()).AsMemory(5), count);
            for (var ordinal = 0; ordinal < count; ordinal++)
            {
                Assert.True(row.TryGetContiguousValue(ordinal, out var field, out isNull));
                Assert.False(isNull);
                Assert.Equal(ordinal, BinaryPrimitives.ReadInt64BigEndian(field));
            }
        }
    }

    private static MpgsqlDataSource Source(ScriptedSession wire, MpgsqlTypeMapper? mapper = null)
        => new MpgsqlDataSource(_ => ValueTask.FromResult(wire.Session), (_, _) => ValueTask.CompletedTask,
            new MpgsqlDataSourceOptions { TypeMapper = mapper });

    private static async Task Sync(ScriptedSession wire)
    {
        var bytes = new List<byte>();
        do { bytes.AddRange(await wire.ReadOutputAsync()); }
        while (!Tags([.. bytes]).Contains('S'));
    }
}
