using System.Buffers;
using System.Reflection;
using Mpgsql.Internal;
using Mpgsql.Protocol;
using Mpgsql.Tests.Protocol;

namespace Mpgsql.Tests.Queries;

public sealed class TypedParameterTests
{
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
