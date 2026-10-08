using System.Buffers;
using System.Text;
using Mpgsql.Internal;
using Mpgsql.Protocol;
using Mpgsql.Tests.Converters;
using Mpgsql.Tests.Protocol;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class StringAliasTests
{
    private static MpgsqlParameter Scalar(uint oid, string? value) => oid switch
    {
        1043 => MpgsqlParameter.VarChar(value),
        1042 => MpgsqlParameter.BpChar(value),
        19 => MpgsqlParameter.Name(value),
        _ => throw new ArgumentOutOfRangeException(nameof(oid))
    };
    private static MpgsqlParameter Array(uint oid, ReadOnlyMemory<string?>? value) => oid switch
    {
        1043 => MpgsqlParameter.VarCharArray(value),
        1042 => MpgsqlParameter.BpCharArray(value),
        19 => MpgsqlParameter.NameArray(value),
        _ => throw new ArgumentOutOfRangeException(nameof(oid))
    };

    [Theory]
    [InlineData(1043u, 1015u)]
    [InlineData(1042u, 1014u)]
    [InlineData(19u, 1003u)]
    public void ParameterPacketUsesExplicitOidsAndIndependentPayloads(uint oid, uint arrayOid)
    {
        const string sql = "select $1, $2, $3, $4, $5, $6";
        MpgsqlParameter[] parameters = [Scalar(oid, "Я😀  "), Array(oid, new string?[] {"Я😀  ", null, ""}),
            Scalar(oid, null), Array(oid, null), Scalar(oid, ""), Array(oid, ReadOnlyMemory<string?>.Empty)];
        byte[] scalar = TestWire.Bytes("d0aff09f98802020");
        var expected = new ArrayBufferWriter<byte>();
        FrontendMessage.Parse(sql, parameterTypes: new[] {oid, arrayOid, oid, arrayOid, oid, arrayOid}).Write(expected);
        FrontendMessage.Bind(parameters: new ReadOnlyMemory<byte>?[]
            {scalar, ConverterAssertions.ArrayBytes(oid, scalar, null, System.Array.Empty<byte>()), null, null,
                System.Array.Empty<byte>(), ConverterAssertions.ArrayBytes(oid)},
            parameterFormats: new[] {FormatCode.Binary}, resultFormats: new[] {FormatCode.Binary}).Write(expected);
        FrontendMessage.Describe(StatementOrPortal.Portal).Write(expected);
        FrontendMessage.Execute().Write(expected);
        byte[] packet = new byte[QueryPacket.GetByteCount(sql, parameters)];
        Assert.Equal(packet.Length, QueryPacket.Write(sql, parameters, packet));
        Assert.Equal(expected.WrittenSpan.ToArray(), packet);
        Assert.Equal(oid, parameters[0].PostgresTypeOid);
        Assert.Equal(arrayOid, parameters[1].PostgresTypeOid);
    }

    [Theory]
    [InlineData(1043u, 1015u)]
    [InlineData(1042u, 1014u)]
    [InlineData(19u, 1003u)]
    public void DecoderPreservesSpacesNullElementsAndEmptyValuesAcrossSegments(uint oid, uint arrayOid)
    {
        byte[] scalar = "Я😀  "u8.ToArray();
        byte[] array = ConverterAssertions.ArrayBytes(oid, scalar, null, System.Array.Empty<byte>());
        Assert.True(FieldValueDecoder<string>.Supports(oid));
        Assert.True(FieldValueDecoder<ReadOnlyMemory<string?>>.Supports(arrayOid));
        Assert.True(FieldValueDecoder<ReadOnlyMemory<string?>?>.Supports(arrayOid));
        Assert.False(FieldValueDecoder<long>.Supports(oid));
        Assert.False(FieldValueDecoder<string>.Supports(arrayOid));
        Assert.Equal("Я😀  ", FieldValueDecoder<string>.Read(oid, TestWire.ByteSegments(scalar)));
        Assert.Equal("", FieldValueDecoder<string>.Read(oid, ReadOnlySequence<byte>.Empty));
        string?[] expected = ["Я😀  ", null, ""];
        Assert.Equal(expected, FieldValueDecoder<ReadOnlyMemory<string?>>.Read(arrayOid, TestWire.ByteSegments(array)).ToArray());
        Assert.Equal(expected, FieldValueDecoder<ReadOnlyMemory<string?>?>.Read(arrayOid, new(array))!.Value.ToArray());
        Assert.True(FieldValueDecoder<ReadOnlyMemory<string?>>.Read(arrayOid,
            new(ConverterAssertions.ArrayBytes(oid))).IsEmpty);
        // A text[] element OID cannot be silently treated as this alias's array.
        Assert.Throws<InvalidDataException>(() => FieldValueDecoder<ReadOnlyMemory<string?>>.Read(arrayOid,
            new(ConverterAssertions.ArrayBytes(25, scalar))));
        Assert.Throws<InvalidDataException>(() => FieldValueDecoder<string>.Read(oid,
            TestWire.ByteSegments(new byte[] {0xf0, 0x9f, 0x98})));
        byte[] destination = Enumerable.Repeat((byte)0xA5, 512).ToArray();
        Assert.ThrowsAny<ArgumentException>(() => QueryPacket.Write("select $1", new[] {Scalar(oid, "\ud800")}, destination));
        Assert.All(destination, value => Assert.Equal(0xA5, value));
    }

    [Theory]
    [InlineData(1043u, 1015u)]
    [InlineData(1042u, 1014u)]
    [InlineData(19u, 1003u)]
    public async Task TypedReaderOwnsAliasValuesAndKeepsConnectionUsableAfterWrongGetter(uint oid, uint arrayOid)
    {
        await using var wire = new ScriptedSession();
        await using var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await batch.SendQueryAsync("select string_aliases"); await batch.SendSyncAsync();
        byte[] scalar = Encoding.UTF8.GetBytes("Я😀  ");
        var writing = wire.WriteAsync(Join(Packet('1'), Packet('2'), Description(oid, arrayOid, oid, arrayOid),
            Row(scalar, ConverterAssertions.ArrayBytes(oid, scalar, null, System.Array.Empty<byte>()), null, null),
            Command(), Ready()), fragment: 1);
        var reader = await batch.ReadResultsAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Throws<InvalidCastException>(() => reader.GetFieldValue<long>(0));
        Assert.Throws<InvalidCastException>(() => reader.GetFieldValue<string>(1));
        string decoded = reader.GetFieldValue<string>(0);
        var decodedArray = reader.GetFieldValue<ReadOnlyMemory<string?>>(1);
        Assert.Null(reader.GetFieldValue<string?>(2));
        Assert.Null(reader.GetFieldValue<ReadOnlyMemory<string?>?>(3));
        Assert.Throws<InvalidOperationException>(() => reader.GetFieldValue<ReadOnlyMemory<string?>>(3));
        Assert.False(await reader.NextResultAsync());
        await reader.DisposeAsync(); await writing;
        Assert.Equal("Я😀  ", decoded);
        Assert.Equal(new string?[] {decoded, null, ""}, decodedArray.ToArray());
        Assert.True(wire.Session.IsHealthy);
        await using var next = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await next.SendQueryAsync("select 9"); await next.SendSyncAsync();
        var nextWriting = wire.WriteAsync(Join(Query(9), Ready()));
        await using var nextReader = await next.ReadResultsAsync();
        Assert.True(await nextReader.ReadAsync()); Assert.Equal(9, nextReader.GetFieldValue<long>(0));
        Assert.False(await nextReader.NextResultAsync()); await nextWriting;
    }
}
