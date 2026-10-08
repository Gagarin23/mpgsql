using System.Buffers;
using Mpgsql.Converters;

namespace Mpgsql.Tests.Converters;

public sealed class MoneyConvertersTests
{
    [Fact]
    public void LiteralPayloadsArraysBuffersAndSegmentBoundaries()
    {
        var value = -12345L;
        ConverterAssertions.CheckScalar(value, "ffffffffffffcfc7", MoneyConverter.GetByteCount, MoneyConverter.Write,
            MoneyConverter.Write, MoneyConverter.Read, MoneyConverter.Read);
        ConverterAssertions.CheckNullableScalar(MoneyConverter.Write, MoneyConverter.Write,
            MoneyConverter.GetByteCount, MoneyConverter.ReadNullable, MoneyConverter.ReadNullable);
        ConverterAssertions.CheckArray(new[] {value, value}, (uint)TypeOid.Money, "ffffffffffffcfc7",
            MoneyArrayConverter.GetByteCount, MoneyArrayConverter.Write, MoneyArrayConverter.Write,
            MoneyArrayConverter.Read, MoneyArrayConverter.Read, MoneyArrayConverter.Read, MoneyArrayConverter.Read);
        ConverterAssertions.CheckNullableArray<long, MoneyCodec>(value, "ffffffffffffcfc7");
    }
    [Fact]
    public void RejectsTruncatedAndTrailingPayloads()
    {
        var bytes = Convert.FromHexString("ffffffffffffcfc7");
        Assert.Throws<InvalidDataException>(() => MoneyConverter.Read(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.Throws<InvalidDataException>(() => MoneyConverter.Read(new ReadOnlySequence<byte>(bytes.AsMemory(0, bytes.Length - 1))));
        byte[] trailing = [.. bytes, 0];
        Assert.Throws<InvalidDataException>(() => MoneyConverter.Read(trailing));
        Assert.Throws<InvalidDataException>(() => MoneyConverter.Read(new ReadOnlySequence<byte>(trailing)));
    }
}