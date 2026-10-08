using System.Buffers;
using System.Text;
using Mpgsql.Converters;
using Mpgsql.Tests.Protocol;

namespace Mpgsql.Tests.Converters;

public sealed class TextConvertersTests
{
    [Fact]
    public void LiteralPayloadsArraysAndSplitUnicode()
    {
        var value = "Я😀";
        ConverterAssertions.CheckScalar
        (
            value, "d0aff09f9880", TextConverter.GetByteCount, TextConverter.Write,
            TextConverter.Write, TextConverter.Read, TextConverter.Read
        );
        ConverterAssertions.CheckArray
        (
            new[]
            {
                value,
                value
            }, (uint)TypeOid.Text, "d0aff09f9880",
            TextArrayConverter.GetByteCount, TextArrayConverter.Write, TextArrayConverter.Write,
            TextArrayConverter.Read, TextArrayConverter.Read, TextArrayConverter.Read, TextArrayConverter.Read
        );
        string?[] nullable = [value, null, ""];
        var payload = new byte[TextArrayConverter.GetByteCount(nullable)];
        TextArrayConverter.Write(nullable, payload);
        Assert.Equal
        (
            nullable, TextArrayConverter
                .Read(payload)
                .ToArray()
        );
        Assert.Equal
        (
            nullable, TextArrayConverter
                .Read(TestWire.ByteSegments(payload))
                .ToArray()
        );
        Assert.Null(TextConverter.ReadNullable((ReadOnlyMemory<byte>?)null));
        Assert.Null(TextConverter.ReadNullable((ReadOnlySequence<byte>?)null));
        Assert.Equal(0, TextConverter.Write(null, Span<byte>.Empty));
        byte[] invalid = [0xf0, 0x9f, 0x98];

        Assert.Throws<InvalidDataException>(() => TextConverter.Read(invalid));
        Assert.Throws<InvalidDataException>(() => TextConverter.Read(TestWire.ByteSegments(invalid)));
        var invalidUtf16 = new string
        (
            new[]
            {
                (char)0xd800
            }
        );
        var destination = Enumerable
            .Repeat((byte)0xcc, 32)
            .ToArray();
        Assert.Throws<EncoderFallbackException>(() => TextConverter.Write(invalidUtf16, destination));
        Assert.All(destination, b => Assert.Equal((byte)0xcc, b));
    }
}