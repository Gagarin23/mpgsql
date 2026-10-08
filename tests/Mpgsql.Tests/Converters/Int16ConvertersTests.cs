using System.Buffers;
using Mpgsql.Converters;

namespace Mpgsql.Tests.Converters;

public sealed class Int16ConvertersTests
{
    [Fact]
    public void LiteralPayloadsArraysBuffersAndSegmentBoundaries()
    {
        var value = (short)-1234;
        ConverterAssertions.CheckScalar
        (
            value, "fb2e", Int16Converter.GetByteCount, Int16Converter.Write,
            Int16Converter.Write, Int16Converter.Read, Int16Converter.Read
        );
        ConverterAssertions.CheckNullableScalar
        (
            Int16Converter.Write, Int16Converter.Write,
            Int16Converter.GetByteCount, Int16Converter.ReadNullable, Int16Converter.ReadNullable
        );
        ConverterAssertions.CheckArray
        (
            new[]
            {
                value,
                value
            }, (uint)TypeOid.Int16, "fb2e",
            Int16ArrayConverter.GetByteCount, Int16ArrayConverter.Write, Int16ArrayConverter.Write,
            Int16ArrayConverter.Read, Int16ArrayConverter.Read, Int16ArrayConverter.Read, Int16ArrayConverter.Read
        );
        ConverterAssertions.CheckNullableArray<short, Int16Codec>(value, "fb2e");
    }
    [Fact]
    public void RejectsTruncatedAndTrailingPayloads()
    {
        var bytes = Convert.FromHexString("fb2e");
        Assert.Throws<InvalidDataException>(() => Int16Converter.Read(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.Throws<InvalidDataException>(() => Int16Converter.Read(new ReadOnlySequence<byte>(bytes.AsMemory(0, bytes.Length - 1))));
        byte[] trailing = [.. bytes, 0];
        Assert.Throws<InvalidDataException>(() => Int16Converter.Read(trailing));
        Assert.Throws<InvalidDataException>(() => Int16Converter.Read(new ReadOnlySequence<byte>(trailing)));
    }
}