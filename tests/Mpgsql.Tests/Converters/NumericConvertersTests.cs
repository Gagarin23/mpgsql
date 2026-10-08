using System.Buffers;
using Mpgsql.Converters;
using Mpgsql.Types;

namespace Mpgsql.Tests.Converters;

public sealed class NumericConvertersTests
{
    [Fact]
    public void LiteralPayloadsArraysBuffersAndSegmentBoundaries()
    {
        var value = new PgNumeric(1, 2, PgNumericSign.Negative, new ushort[] {1, 2345, 6700});
        ConverterAssertions.CheckScalar(value, "0003000140000002000109291a2c", NumericConverter.GetByteCount, NumericConverter.Write,
            NumericConverter.Write, NumericConverter.Read, NumericConverter.Read);
        ConverterAssertions.CheckNullableScalar(NumericConverter.Write, NumericConverter.Write,
            NumericConverter.GetByteCount, NumericConverter.ReadNullable, NumericConverter.ReadNullable);
        ConverterAssertions.CheckArray(new[] {value, value}, (uint)TypeOid.Numeric, "0003000140000002000109291a2c",
            NumericArrayConverter.GetByteCount, NumericArrayConverter.Write, NumericArrayConverter.Write,
            NumericArrayConverter.Read, NumericArrayConverter.Read, NumericArrayConverter.Read, NumericArrayConverter.Read);
        ConverterAssertions.CheckNullableArray<PgNumeric, NumericCodec>(value, "0003000140000002000109291a2c");
    }
    [Fact]
    public void RejectsTruncatedAndTrailingPayloads()
    {
        var bytes = Convert.FromHexString("0003000140000002000109291a2c");
        Assert.Throws<InvalidDataException>(() => NumericConverter.Read(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.Throws<InvalidDataException>(() => NumericConverter.Read(new ReadOnlySequence<byte>(bytes.AsMemory(0, bytes.Length - 1))));
        byte[] trailing = [.. bytes, 0];
        Assert.Throws<InvalidDataException>(() => NumericConverter.Read(trailing));
        Assert.Throws<InvalidDataException>(() => NumericConverter.Read(new ReadOnlySequence<byte>(trailing)));
    }
}