using System.Buffers;
using System.Net;
using Mpgsql.Converters;
using Mpgsql.Types;

namespace Mpgsql.Tests.Converters;

public sealed class Int32ConvertersTests
{
    [Fact]
    public void LiteralPayloadsArraysBuffersAndSegmentBoundaries()
    {
        int value = -123456789;
        ConverterAssertions.CheckScalar(value, "f8a432eb", Int32Converter.GetByteCount, Int32Converter.Write,
            Int32Converter.Write, Int32Converter.Read, Int32Converter.Read);
        ConverterAssertions.CheckNullableScalar<int>(Int32Converter.Write, Int32Converter.Write,
            Int32Converter.GetByteCount, Int32Converter.ReadNullable, Int32Converter.ReadNullable);
        ConverterAssertions.CheckArray(new int[] {value, value}, (uint)TypeOid.Int32, "f8a432eb",
            Int32ArrayConverter.GetByteCount, Int32ArrayConverter.Write, Int32ArrayConverter.Write,
            Int32ArrayConverter.Read, Int32ArrayConverter.Read, Int32ArrayConverter.Read, Int32ArrayConverter.Read);
        ConverterAssertions.CheckNullableArray<int, Int32Codec>(value, "f8a432eb");
    }
    [Fact]
    public void RejectsTruncatedAndTrailingPayloads()
    {
        byte[] bytes = Convert.FromHexString("f8a432eb");
        Assert.Throws<InvalidDataException>(() => Int32Converter.Read(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.Throws<InvalidDataException>(() => Int32Converter.Read(new ReadOnlySequence<byte>(bytes.AsMemory(0, bytes.Length - 1))));
        byte[] trailing = [.. bytes, 0];
        Assert.Throws<InvalidDataException>(() => Int32Converter.Read(trailing));
        Assert.Throws<InvalidDataException>(() => Int32Converter.Read(new ReadOnlySequence<byte>(trailing)));
    }
}