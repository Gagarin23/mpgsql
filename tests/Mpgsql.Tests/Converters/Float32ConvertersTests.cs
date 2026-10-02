using System.Buffers;
using System.Net;
using Mpgsql.Converters;
using Mpgsql.Types;

namespace Mpgsql.Tests.Converters;

public sealed class Float32ConvertersTests
{
    [Fact]
    public void LiteralPayloadsArraysBuffersAndSegmentBoundaries()
    {
        float value = 1.5f;
        ConverterAssertions.CheckScalar(value, "3fc00000", Float32Converter.GetByteCount, Float32Converter.Write,
            Float32Converter.Write, Float32Converter.Read, Float32Converter.Read);
        ConverterAssertions.CheckNullableScalar<float>(Float32Converter.Write, Float32Converter.Write,
            Float32Converter.GetByteCount, Float32Converter.ReadNullable, Float32Converter.ReadNullable);
        ConverterAssertions.CheckArray(new float[] {value, value}, (uint)TypeOid.Float32, "3fc00000",
            Float32ArrayConverter.GetByteCount, Float32ArrayConverter.Write, Float32ArrayConverter.Write,
            Float32ArrayConverter.Read, Float32ArrayConverter.Read, Float32ArrayConverter.Read, Float32ArrayConverter.Read);
        ConverterAssertions.CheckNullableArray<float, Float32Codec>(value, "3fc00000");
    }
    [Fact]
    public void RejectsTruncatedAndTrailingPayloads()
    {
        byte[] bytes = Convert.FromHexString("3fc00000");
        Assert.Throws<InvalidDataException>(() => Float32Converter.Read(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.Throws<InvalidDataException>(() => Float32Converter.Read(new ReadOnlySequence<byte>(bytes.AsMemory(0, bytes.Length - 1))));
        byte[] trailing = [.. bytes, 0];
        Assert.Throws<InvalidDataException>(() => Float32Converter.Read(trailing));
        Assert.Throws<InvalidDataException>(() => Float32Converter.Read(new ReadOnlySequence<byte>(trailing)));
    }
}