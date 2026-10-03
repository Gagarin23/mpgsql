using System.Buffers;
using Mpgsql.Converters;
using Mpgsql.Tests.Protocol;

namespace Mpgsql.Tests.Converters;

public sealed class XmlConvertersTests
{
    [Fact]
    public void LiteralPayloadsArraysAndSplitUnicode()
    {
        string value = "<a>Я😀</a>";
        ConverterAssertions.CheckScalar(value, "3c613ed0aff09f98803c2f613e", XmlConverter.GetByteCount, XmlConverter.Write,
            XmlConverter.Write, XmlConverter.Read, XmlConverter.Read);
        ConverterAssertions.CheckArray(new string?[] {value, value}, (uint)TypeOid.Xml, "3c613ed0aff09f98803c2f613e",
            XmlArrayConverter.GetByteCount, XmlArrayConverter.Write, XmlArrayConverter.Write,
            XmlArrayConverter.Read, XmlArrayConverter.Read, XmlArrayConverter.Read, XmlArrayConverter.Read);
        string?[] nullable = [value, null, ""];
        byte[] payload = new byte[XmlArrayConverter.GetByteCount(nullable)];
        XmlArrayConverter.Write(nullable, payload);
        Assert.Equal(nullable, XmlArrayConverter.Read(payload).ToArray());
        Assert.Equal(nullable, XmlArrayConverter.Read(TestWire.ByteSegments(payload)).ToArray());
        Assert.Null(XmlConverter.ReadNullable((ReadOnlyMemory<byte>?)null));
        Assert.Null(XmlConverter.ReadNullable((ReadOnlySequence<byte>?)null));
        Assert.Equal(0, XmlConverter.Write(null, Span<byte>.Empty));
        byte[] invalid = [0xf0, 0x9f, 0x98];

        Assert.Throws<InvalidDataException>(() => XmlConverter.Read(invalid));
        Assert.Throws<InvalidDataException>(() => XmlConverter.Read(TestWire.ByteSegments(invalid)));
        string invalidUtf16 = new(new[] {(char)0xd800});
        byte[] destination = Enumerable.Repeat((byte)0xcc, 32).ToArray();
        Assert.Throws<System.Text.EncoderFallbackException>(() => XmlConverter.Write(invalidUtf16, destination));
        Assert.All(destination, b => Assert.Equal((byte)0xcc, b));
    }
}
