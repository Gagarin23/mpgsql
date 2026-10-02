using System.Buffers;
using System.Net;
using Mpgsql.Converters;
using Mpgsql.Types;

namespace Mpgsql.Tests.Converters;

public sealed class MoneyConvertersTests
{
    [Fact]
    public void LiteralPayloadsArraysBuffersAndSegmentBoundaries()
    {
        long value = -12345L;
        ConverterAssertions.CheckScalar(value, "ffffffffffffcfc7", MoneyConverter.GetByteCount, MoneyConverter.Write,
            MoneyConverter.Write, MoneyConverter.Read, MoneyConverter.Read);
        ConverterAssertions.CheckNullableScalar<long>(MoneyConverter.Write, MoneyConverter.Write,
            MoneyConverter.GetByteCount, MoneyConverter.ReadNullable, MoneyConverter.ReadNullable);
        ConverterAssertions.CheckArray(new long[] {value, value}, (uint)TypeOid.Money, "ffffffffffffcfc7",
            MoneyArrayConverter.GetByteCount, MoneyArrayConverter.Write, MoneyArrayConverter.Write,
            MoneyArrayConverter.Read, MoneyArrayConverter.Read, MoneyArrayConverter.Read, MoneyArrayConverter.Read);
        ConverterAssertions.CheckNullableArray<long, MoneyCodec>(value, "ffffffffffffcfc7");
    }
    [Fact]
    public void RejectsTruncatedAndTrailingPayloads()
    {
        byte[] bytes = Convert.FromHexString("ffffffffffffcfc7");
        Assert.Throws<InvalidDataException>(() => MoneyConverter.Read(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.Throws<InvalidDataException>(() => MoneyConverter.Read(new ReadOnlySequence<byte>(bytes.AsMemory(0, bytes.Length - 1))));
        byte[] trailing = [.. bytes, 0];
        Assert.Throws<InvalidDataException>(() => MoneyConverter.Read(trailing));
        Assert.Throws<InvalidDataException>(() => MoneyConverter.Read(new ReadOnlySequence<byte>(trailing)));
    }
}