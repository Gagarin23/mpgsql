using System.Buffers;
using System.Reflection;
using Mpgsql.Tests.Converters;
using Mpgsql.Internal;
using Mpgsql.Protocol;
using Mpgsql.Tests.Protocol;

namespace Mpgsql.Tests.Queries;

public sealed class TypedParameterTests
{
    [Fact]
    public void JsonbMemoryParametersMatchIndependentBinaryMessages()
    {
        const string sql = "select $1, $2, $3, $4, $5, $6, $7";
        Memory<byte> value = "{\"x\":\"Я😀\"}"u8.ToArray();
        MpgsqlParameter[] parameters = [MpgsqlParameter.Jsonb(value),
            MpgsqlParameter.JsonbArray(new Memory<byte>[] {value, value}),
            MpgsqlParameter.NullableJsonbArray(new Memory<byte>?[] {value, null}),
            MpgsqlParameter.Jsonb(null), MpgsqlParameter.Jsonb(Memory<byte>.Empty),
            MpgsqlParameter.JsonbArray(ReadOnlyMemory<Memory<byte>>.Empty), MpgsqlParameter.NullableJsonbArray(null)];
        byte[] scalar = TestWire.Bytes("017b2278223a22d0aff09f9880227d");
        var reference = new ArrayBufferWriter<byte>();
        FrontendMessage.Parse(sql, parameterTypes: new uint[] {3802, 3807, 3807, 3802, 3802, 3807, 3807}).Write(reference);
        FrontendMessage.Bind(parameters: new ReadOnlyMemory<byte>?[]
            {scalar, ConverterAssertions.ArrayBytes(3802, scalar, scalar), ConverterAssertions.ArrayBytes(3802, scalar, null),
                null, new byte[] {1}, ConverterAssertions.ArrayBytes(3802), null},
            parameterFormats: new[] {FormatCode.Binary}, resultFormats: new[] {FormatCode.Binary}).Write(reference);
        FrontendMessage.Describe(StatementOrPortal.Portal).Write(reference);
        FrontendMessage.Execute().Write(reference);
        byte[] packet = new byte[QueryPacket.GetByteCount(sql, parameters)];
        QueryPacket.Write(sql, parameters, packet);
        Assert.Equal(reference.WrittenSpan.ToArray(), packet);
    }

    [Fact]
    public void RawJsonbParametersAreEncodedWithoutContentValidation()
    {
        Memory<byte> invalid = new byte[] {0xf0, 0x9f, 0x98};
        byte[] target = Enumerable.Repeat((byte)0xA5, 256).ToArray();
        MpgsqlParameter[] values = [MpgsqlParameter.Jsonb(invalid),
            MpgsqlParameter.JsonbArray(new Memory<byte>[] {"null"u8.ToArray(), invalid}),
            MpgsqlParameter.NullableJsonbArray(new Memory<byte>?[] {null, invalid})];
        byte[] scalar = [1, .. invalid.Span];
        byte[][] payloads = [scalar, ConverterAssertions.ArrayBytes(3802, "\u0001null"u8.ToArray(), scalar),
            ConverterAssertions.ArrayBytes(3802, null, scalar)];
        for (int i = 0; i < values.Length; i++)
        {
            target.AsSpan().Fill(0xA5);
            var reference = new ArrayBufferWriter<byte>();
            FrontendMessage.Parse("select $1", parameterTypes: new[] {values[i].PostgresTypeOid}).Write(reference);
            FrontendMessage.Bind(parameters: new ReadOnlyMemory<byte>?[] {payloads[i]},
                parameterFormats: new[] {FormatCode.Binary}, resultFormats: new[] {FormatCode.Binary}).Write(reference);
            FrontendMessage.Describe(StatementOrPortal.Portal).Write(reference);
            FrontendMessage.Execute().Write(reference);
            int length = QueryPacket.Write("select $1", new[] {values[i]}, target);
            Assert.Equal(reference.WrittenSpan.ToArray(), target[..length]);
            Assert.All(target[length..], b => Assert.Equal(0xA5, b));
        }
    }

    [Fact]
    public void MixedTypedPacketMatchesIndependentBinaryMessages()
    {
        const string sql = "select $1, $2, $3, $4";
        MpgsqlParameter[] parameters = [MpgsqlParameter.Int32(-7), MpgsqlParameter.Text("Я"),
            MpgsqlParameter.Uuid(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff")), MpgsqlParameter.Bytea(null)];
        var reference = new ArrayBufferWriter<byte>();
        FrontendMessage.Parse(sql, parameterTypes: new uint[] {23, 25, 2950, 17}).Write(reference);
        FrontendMessage.Bind(parameters: new ReadOnlyMemory<byte>?[]
            {TestWire.Bytes("fffffff9"), TestWire.Bytes("d0af"), TestWire.Bytes("00112233445566778899aabbccddeeff"), null},
            parameterFormats: new[] {FormatCode.Binary}, resultFormats: new[] {FormatCode.Binary}).Write(reference);
        FrontendMessage.Describe(StatementOrPortal.Portal).Write(reference); FrontendMessage.Execute().Write(reference);
        byte[] packet = new byte[QueryPacket.GetByteCount(sql, parameters)]; QueryPacket.Write(sql, parameters, packet);
        Assert.Equal(reference.WrittenSpan.ToArray(), packet);
    }

    [Fact]
    public void VariableSizedArrayParametersMatchLiteralElementFraming()
    {
        const string sql = "select $1, $2, $3";
        string?[] text = ["", "Я", null, "😀"];
        decimal?[] numbers = [0, 1, null];
        ReadOnlyMemory<byte>?[] bytes = [ReadOnlyMemory<byte>.Empty, null, new byte[] {1, 255, 128}];
        MpgsqlParameter[] parameters = [MpgsqlParameter.TextArray(text), MpgsqlParameter.NullableDecimalArray(numbers),
            MpgsqlParameter.NullableByteaArray(bytes)];
        var reference = new ArrayBufferWriter<byte>();
        FrontendMessage.Parse(sql, parameterTypes: new uint[] {1009, 1231, 1001}).Write(reference);
        FrontendMessage.Bind(parameters: new ReadOnlyMemory<byte>?[]
            {
                ConverterAssertions.ArrayBytes(25, [], TestWire.Bytes("d0af"), null, TestWire.Bytes("f09f9880")),
                ConverterAssertions.ArrayBytes(1700, TestWire.Bytes("0000000000000000"), TestWire.Bytes("00010000000000000001"), null),
                ConverterAssertions.ArrayBytes(17, [], null, new byte[] {1, 255, 128})
            }, parameterFormats: new[] {FormatCode.Binary}, resultFormats: new[] {FormatCode.Binary}).Write(reference);
        FrontendMessage.Describe(StatementOrPortal.Portal).Write(reference);
        FrontendMessage.Execute().Write(reference);
        byte[] packet = Enumerable.Repeat((byte)0xcc, reference.WrittenCount + 8).ToArray();
        Assert.Equal(reference.WrittenCount, QueryPacket.GetByteCount(sql, parameters));
        Assert.Equal(reference.WrittenCount, QueryPacket.Write(sql, parameters, packet));
        Assert.Equal(reference.WrittenSpan.ToArray(), packet[..reference.WrittenCount]);
        Assert.All(packet[reference.WrittenCount..], value => Assert.Equal((byte)0xcc, value));
    }

    [Fact]
    public void EveryOidHasExplicitNullFactoryAndArrayFactory()
    {
        foreach (TypeOid oid in Enum.GetValues<TypeOid>())
        {
            MethodInfo factory = typeof(MpgsqlParameter).GetMethod(oid.ToString(), BindingFlags.Public | BindingFlags.Static)!;
            Assert.NotNull(factory);
            var parameter = (MpgsqlParameter)factory.Invoke(null, new object?[] {null})!;
            Assert.Equal((uint)oid, parameter.PostgresTypeOid); Assert.Equal(-1, parameter.PayloadLength);
            byte[] target = new byte[QueryPacket.GetByteCount("select $1", new[] {parameter})];
            Assert.Equal(target.Length, QueryPacket.Write("select $1", new[] {parameter}, target));
        }
    }

    [Fact]
    public void InvalidUtf16IsRejectedBeforeDestinationMutation()
    {
        byte[] target = Enumerable.Repeat((byte)0xA5, 256).ToArray();
        Assert.ThrowsAny<ArgumentException>(() => QueryPacket.Write("select $1", new[] {MpgsqlParameter.Text("\ud800")}, target));
        Assert.All(target, b => Assert.Equal(0xA5, b));
    }
}
