using System.Buffers;
using System.Net;
using Mpgsql.Converters;
using Mpgsql.Types;

namespace Mpgsql.Tests.Converters;

public sealed class Float64ConvertersTests
{
    [Fact]
    public void LiteralPayloadsArraysBuffersAndSegmentBoundaries()
    {
        double value = -2.5d;
        ConverterAssertions.CheckScalar(value, "c004000000000000", Float64Converter.GetByteCount, Float64Converter.Write,
            Float64Converter.Write, Float64Converter.Read, Float64Converter.Read);
        ConverterAssertions.CheckNullableScalar<double>(Float64Converter.Write, Float64Converter.Write,
            Float64Converter.GetByteCount, Float64Converter.ReadNullable, Float64Converter.ReadNullable);
        ConverterAssertions.CheckArray(new double[] {value, value}, (uint)TypeOid.Float64, "c004000000000000",
            Float64ArrayConverter.GetByteCount, Float64ArrayConverter.Write, Float64ArrayConverter.Write,
            Float64ArrayConverter.Read, Float64ArrayConverter.Read, Float64ArrayConverter.Read, Float64ArrayConverter.Read);
        ConverterAssertions.CheckNullableArray<double, Float64Codec>(value, "c004000000000000");
    }
    [Fact]
    public void RejectsTruncatedAndTrailingPayloads()
    {
        byte[] bytes = Convert.FromHexString("c004000000000000");
        Assert.Throws<InvalidDataException>(() => Float64Converter.Read(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.Throws<InvalidDataException>(() => Float64Converter.Read(new ReadOnlySequence<byte>(bytes.AsMemory(0, bytes.Length - 1))));
        byte[] trailing = [.. bytes, 0];
        Assert.Throws<InvalidDataException>(() => Float64Converter.Read(trailing));
        Assert.Throws<InvalidDataException>(() => Float64Converter.Read(new ReadOnlySequence<byte>(trailing)));
    }
}