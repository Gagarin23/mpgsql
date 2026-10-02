using System.Buffers;
using System.Net;
using Mpgsql.Converters;
using Mpgsql.Types;

namespace Mpgsql.Tests.Converters;

public sealed class BooleanConvertersTests
{
    [Fact]
    public void LiteralPayloadsArraysBuffersAndSegmentBoundaries()
    {
        bool value = true;
        ConverterAssertions.CheckScalar(value, "01", BooleanConverter.GetByteCount, BooleanConverter.Write,
            BooleanConverter.Write, BooleanConverter.Read, BooleanConverter.Read);
        ConverterAssertions.CheckNullableScalar<bool>(BooleanConverter.Write, BooleanConverter.Write,
            BooleanConverter.GetByteCount, BooleanConverter.ReadNullable, BooleanConverter.ReadNullable);
        ConverterAssertions.CheckArray(new bool[] {value, value}, (uint)TypeOid.Boolean, "01",
            BooleanArrayConverter.GetByteCount, BooleanArrayConverter.Write, BooleanArrayConverter.Write,
            BooleanArrayConverter.Read, BooleanArrayConverter.Read, BooleanArrayConverter.Read, BooleanArrayConverter.Read);
        ConverterAssertions.CheckNullableArray<bool, BooleanCodec>(value, "01");
    }
    [Fact]
    public void RejectsTruncatedAndTrailingPayloads()
    {
        byte[] bytes = Convert.FromHexString("01");
        Assert.Throws<InvalidDataException>(() => BooleanConverter.Read(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.Throws<InvalidDataException>(() => BooleanConverter.Read(new ReadOnlySequence<byte>(bytes.AsMemory(0, bytes.Length - 1))));
        byte[] trailing = [.. bytes, 0];
        Assert.Throws<InvalidDataException>(() => BooleanConverter.Read(trailing));
        Assert.Throws<InvalidDataException>(() => BooleanConverter.Read(new ReadOnlySequence<byte>(trailing)));
    }
}