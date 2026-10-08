using System.Buffers;
using Mpgsql.Converters;

namespace Mpgsql.Tests.Converters;

public sealed class BooleanConvertersTests
{
    [Fact]
    public void LiteralPayloadsArraysBuffersAndSegmentBoundaries()
    {
        var value = true;
        ConverterAssertions.CheckScalar(value, "01", BooleanConverter.GetByteCount, BooleanConverter.Write,
            BooleanConverter.Write, BooleanConverter.Read, BooleanConverter.Read);
        ConverterAssertions.CheckNullableScalar(BooleanConverter.Write, BooleanConverter.Write,
            BooleanConverter.GetByteCount, BooleanConverter.ReadNullable, BooleanConverter.ReadNullable);
        ConverterAssertions.CheckArray(new[] {value, value}, (uint)TypeOid.Boolean, "01",
            BooleanArrayConverter.GetByteCount, BooleanArrayConverter.Write, BooleanArrayConverter.Write,
            BooleanArrayConverter.Read, BooleanArrayConverter.Read, BooleanArrayConverter.Read, BooleanArrayConverter.Read);
        ConverterAssertions.CheckNullableArray<bool, BooleanCodec>(value, "01");
    }
    [Fact]
    public void RejectsTruncatedAndTrailingPayloads()
    {
        var bytes = Convert.FromHexString("01");
        Assert.Throws<InvalidDataException>(() => BooleanConverter.Read(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.Throws<InvalidDataException>(() => BooleanConverter.Read(new ReadOnlySequence<byte>(bytes.AsMemory(0, bytes.Length - 1))));
        byte[] trailing = [.. bytes, 0];
        Assert.Throws<InvalidDataException>(() => BooleanConverter.Read(trailing));
        Assert.Throws<InvalidDataException>(() => BooleanConverter.Read(new ReadOnlySequence<byte>(trailing)));
    }
}