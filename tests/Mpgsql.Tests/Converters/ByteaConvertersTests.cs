using Mpgsql.Converters;

namespace Mpgsql.Tests.Converters;

public sealed class ByteaConvertersTests
{
    [Fact]
    public void LiteralPayloadsArraysBuffersAndSegmentBoundaries()
    {
        var value = new ReadOnlyMemory<byte>(new byte[] {0, 1, 128, 255});
        ConverterAssertions.CheckScalar(value, "000180ff", ByteaConverter.GetByteCount, ByteaConverter.Write,
            ByteaConverter.Write, ByteaConverter.Read, ByteaConverter.Read);
        ConverterAssertions.CheckNullableScalar(ByteaConverter.Write, ByteaConverter.Write,
            ByteaConverter.GetByteCount, ByteaConverter.ReadNullable, ByteaConverter.ReadNullable);
        ConverterAssertions.CheckArray(new[] {value, value}, (uint)TypeOid.Bytea, "000180ff",
            ByteaArrayConverter.GetByteCount, ByteaArrayConverter.Write, ByteaArrayConverter.Write,
            ByteaArrayConverter.Read, ByteaArrayConverter.Read, ByteaArrayConverter.Read, ByteaArrayConverter.Read);
        ConverterAssertions.CheckNullableArray<ReadOnlyMemory<byte>, ByteaCodec>(value, "000180ff");
    }
}