using System.Buffers;
using System.Data.Common;
using Mpgsql.Protocol;
using Mpgsql.Tests.Converters;
using Mpgsql.Tests.Protocol;
using Mpgsql.Types;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class ObjectResultValueContractTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static MpgsqlDataSource Source(ScriptedSession wire, MpgsqlTypeMapper? mapper = null)
        => new MpgsqlDataSource(_ => ValueTask.FromResult(wire.Session), (_, _) => ValueTask.CompletedTask,
            new MpgsqlDataSourceOptions { TypeMapper = mapper });

    private static async Task AssertRequestAsync(ScriptedSession wire, string sql)
    {
        var bytes = new List<byte>();
        do { bytes.AddRange(await wire.ReadOutputAsync()); }
        while (!Tags([.. bytes]).Contains('S'));
        var expected = new ArrayBufferWriter<byte>();
        FrontendMessage.Parse(sql).Write(expected);
        FrontendMessage.Bind(parameterFormats: new[] { FormatCode.Binary }, resultFormats: new[] { FormatCode.Binary }).Write(expected);
        FrontendMessage.Describe(StatementOrPortal.Portal).Write(expected);
        FrontendMessage.Execute().Write(expected);
        FrontendMessage.Sync().Write(expected);
        Assert.Equal(expected.WrittenSpan.ToArray(), bytes.ToArray());
    }

    [Theory, InlineData(1), InlineData(17), InlineData(int.MaxValue)]
    public async Task DbObjectReadsPreserveNullEmptyAndOwnedRepresentationsAcrossRows(int fragment)
    {
        await using var wire = new ScriptedSession();
        await using DbDataSource source = Source(wire);
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "select object values";
        var opening = command.ExecuteReaderAsync(Token);
        await AssertRequestAsync(wire, command.CommandText);
        byte[] bytea = [.. Enumerable.Range(0, 64).Select(i => (byte)i)];
        byte[] json = "{\"value\":\"Я🙂\"}"u8.ToArray();
        long?[] array = [7, null, long.MaxValue];
        var writing = wire.WriteAsync
        (
            Join
            (
                Begin(20, 17, 3802, 1016, 1042, 1700),
                Row
                (
                    Int64(long.MinValue), bytea, [1, .. json],
                    ConverterAssertions.ArrayBytes(20, Int64(7), null, Int64(long.MaxValue)),
                    "Я  "u8.ToArray(), TestWire.Bytes("0002 0000 4000 0004 007b 1194")
                ),
                Row(null, null, null, null, null, null),
                Row(Int64(0), [], [1], ConverterAssertions.ArrayBytes(20), [], TestWire.Bytes("0000 0000 0000 0000")),
                Command("SELECT 3"), Ready()
            ), fragment
        );
        await using var reader = await opening.WaitAsync(TestTimeout, Token);
        Assert.Throws<InvalidOperationException>(() => reader.GetValue(0));
        Assert.True(await reader.ReadAsync(Token));
        Assert.Equal(long.MinValue, Assert.IsType<long>(reader.GetValue(0)));
        Assert.Equal(long.MinValue, reader.GetFieldValue<object>(0));
        var retainedBytea = Assert.IsType<ReadOnlyMemory<byte>>(reader.GetValue(1));
        var retainedJson = Assert.IsType<Memory<byte>>(reader.GetValue(2));
        var retainedArray = Assert.IsType<ReadOnlyMemory<long?>>(reader.GetValue(3));
        var retainedText = Assert.IsType<string>(reader.GetValue(4));
        var retainedNumeric = Assert.IsType<PgNumeric>(reader.GetValue(5));
        Assert.Equal(bytea, retainedBytea.ToArray());
        Assert.Equal(json, retainedJson.ToArray());
        Assert.Equal(array, retainedArray.ToArray());
        Assert.Equal("Я  ", retainedText);
        Assert.Equal(-123.45m, retainedNumeric.ToDecimal());
        Assert.Equal((ushort)4, retainedNumeric.Scale);
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.GetValue(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.GetValue(6));
        var sentinel = new object();
        var values = Enumerable.Repeat(sentinel, 7).ToArray();
        Assert.Equal(6, reader.GetValues(values));
        Assert.Equal(long.MinValue, values[0]);
        Assert.Equal(bytea, Assert.IsType<ReadOnlyMemory<byte>>(values[1]).ToArray());
        Assert.Equal(json, Assert.IsType<Memory<byte>>(values[2]).ToArray());
        Assert.Equal(array, Assert.IsType<ReadOnlyMemory<long?>>(values[3]).ToArray());
        Assert.Equal(retainedText, values[4]);
        Assert.Equal(-123.45m, Assert.IsType<PgNumeric>(values[5]).ToDecimal());
        Assert.Same(sentinel, values[6]);

        Assert.True(await reader.ReadAsync(Token));
        Assert.Equal(6, reader.GetValues(values));
        for (var ordinal = 0; ordinal < 6; ordinal++)
        {
            Assert.Same(DBNull.Value, reader.GetValue(ordinal));
            Assert.Same(DBNull.Value, reader.GetFieldValue<object>(ordinal));
            Assert.Same(DBNull.Value, values[ordinal]);
        }
        Assert.Same(sentinel, values[6]);
        Assert.True(await reader.ReadAsync(Token));
        Assert.Equal(0L, reader.GetValue(0));
        Assert.True(Assert.IsType<ReadOnlyMemory<byte>>(reader.GetValue(1)).IsEmpty);
        Assert.True(Assert.IsType<Memory<byte>>(reader.GetValue(2)).IsEmpty);
        Assert.True(Assert.IsType<ReadOnlyMemory<long?>>(reader.GetValue(3)).IsEmpty);
        Assert.Equal(string.Empty, reader.GetValue(4));
        Assert.Equal(0m, Assert.IsType<PgNumeric>(reader.GetValue(5)).ToDecimal());
        Assert.False(await reader.ReadAsync(Token));
        Assert.Throws<InvalidOperationException>(() => reader.GetValue(0));
        Assert.False(await reader.NextResultAsync(Token));
        await writing.WaitAsync(TestTimeout, Token);
        await reader.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => reader.GetValue(0));
        Assert.Equal(bytea, retainedBytea.ToArray());
        Assert.Equal(json, retainedJson.ToArray());
        Assert.Equal(array, retainedArray.ToArray());
        Assert.Equal("Я  ", retainedText);
        Assert.Equal(-123.45m, retainedNumeric.ToDecimal());
        Assert.Equal(0, wire.Session.BufferedRowBytes);
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task UnsupportedOidStillReturnsDbNullAndRequiresRawAccessForNonNull(bool withCustomMapping)
    {
        var mappedReads = 0;
        var mapper = new MpgsqlTypeMapper().Register<long>(90000, _ =>
        {
            mappedReads++;
            return 42L;
        });
        await using var wire = new ScriptedSession();
        await using DbDataSource source = Source(wire, withCustomMapping ? mapper : null);
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "select custom values";
        var opening = command.ExecuteReaderAsync(Token);
        await AssertRequestAsync(wire, command.CommandText);
        var writing = wire.WriteAsync(Join(Begin(90000), Row((byte[]?)null), Row(new byte[] { 0, 255, 1 }), Command("SELECT 2"), Ready()), 1);
        await using var reader = await opening.WaitAsync(TestTimeout, Token);
        Assert.True(await reader.ReadAsync(Token));
        Assert.Same(DBNull.Value, reader.GetValue(0));
        Assert.Same(DBNull.Value, reader.GetFieldValue<object>(0));
        Assert.True(await reader.ReadAsync(Token));
        Assert.Throws<NotSupportedException>(() => reader.GetValue(0));
        Assert.Throws<NotSupportedException>(() => reader.GetFieldValue<object>(0));
        Assert.Equal(new byte[] { 0, 255, 1 }, ((MpgsqlDataReader)reader).GetRawValue(0)!.Value.ToArray());
        Assert.Equal(0, mappedReads); // Object reads keep the fixed built-in representations.
        if (withCustomMapping)
        {
            Assert.Equal(42L, reader.GetFieldValue<long>(0));
            Assert.Equal(1, mappedReads);
        }
        Assert.False(await reader.NextResultAsync(Token));
        await writing.WaitAsync(TestTimeout, Token);
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Theory, InlineData(20, 0), InlineData(20, 7), InlineData(20, 9), InlineData(3802, 1)]
    public async Task ObjectReadRetainsDecoderFramingChecksWithoutConsumingTheRow(int oid, int size)
    {
        await using var wire = new ScriptedSession();
        await using DbDataSource source = Source(wire);
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "select malformed binary value";
        var opening = command.ExecuteReaderAsync(Token);
        await AssertRequestAsync(wire, command.CommandText);
        byte[] malformed = oid == 20 ? new byte[size] : [2];
        var writing = wire.WriteAsync(Join(Begin((uint)oid), Row(malformed), Command(), Ready()), 1);
        await using var reader = await opening.WaitAsync(TestTimeout, Token);
        Assert.True(await reader.ReadAsync(Token));
        Assert.Throws<InvalidDataException>(() => reader.GetValue(0));
        Assert.Throws<InvalidDataException>(() => reader.GetFieldValue<object>(0));
        Assert.Equal(malformed, ((MpgsqlDataReader)reader).GetRawValue(0)!.Value.ToArray());
        Assert.False(await reader.NextResultAsync(Token));
        await writing.WaitAsync(TestTimeout, Token);
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Fact]
    public async Task TextFormatIsRejectedBeforeObjectReaderMetadataIsPublished()
    {
        await using var wire = new ScriptedSession();
        await using DbDataSource source = Source(wire);
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "select text format";
        var opening = command.ExecuteReaderAsync(Token);
        await AssertRequestAsync(wire, command.CommandText);
        var description = Description(20);
        description[^2] = description[^1] = 0; // The last RowField format is network-order int16.
        var writing = wire.WriteAsync(Join(Packet('1'), Packet('2'), description));
        var error = await Assert.ThrowsAsync<MpgsqlException>(
            () => opening.WaitAsync(TestTimeout, Token));
        Assert.IsType<InvalidDataException>(error.InnerException);
        // A broken input pipe may also fault the scripted server's Flush. Observe
        // that task separately; only the provider operation crosses the ADO boundary.
        _ = await Record.ExceptionAsync(() => writing.WaitAsync(TestTimeout, Token));
        Assert.False(wire.Session.IsHealthy);
    }
}
