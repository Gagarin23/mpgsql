using System.Buffers;
using Mpgsql.Converters;
using Mpgsql.Tests.Protocol;

namespace Mpgsql.Tests.Converters;

public sealed class JsonbConvertersTests
{
    [Fact]
    public void LiteralPayloadsArraysAndSplitUnicode()
    {
        string value = "{\"x\":\"Я😀\"}";
        ConverterAssertions.CheckScalar(value, "017b2278223a22d0aff09f9880227d", JsonbConverter.GetByteCount, JsonbConverter.Write,
            JsonbConverter.Write, JsonbConverter.Read, JsonbConverter.Read);
        ConverterAssertions.CheckArray(new string?[] {value, value}, (uint)TypeOid.Jsonb, "017b2278223a22d0aff09f9880227d",
            JsonbArrayConverter.GetByteCount, JsonbArrayConverter.Write, JsonbArrayConverter.Write,
            JsonbArrayConverter.Read, JsonbArrayConverter.Read, JsonbArrayConverter.Read, JsonbArrayConverter.Read);
        string?[] nullable = [value, null, ""];
        byte[] payload = new byte[JsonbArrayConverter.GetByteCount(nullable)];
        JsonbArrayConverter.Write(nullable, payload);
        Assert.Equal(nullable, JsonbArrayConverter.Read(payload).ToArray());
        Assert.Equal(nullable, JsonbArrayConverter.Read(TestWire.ByteSegments(payload)).ToArray());
        Assert.Null(JsonbConverter.ReadNullable((ReadOnlyMemory<byte>?)null));
        Assert.Null(JsonbConverter.ReadNullable((ReadOnlySequence<byte>?)null));
        Assert.Equal(0, JsonbConverter.Write(null, Span<byte>.Empty));
        byte[] invalid = [0xf0, 0x9f, 0x98];
        invalid = [1, .. invalid];
        Assert.Throws<InvalidDataException>(() => JsonbConverter.Read(invalid));
        Assert.Throws<InvalidDataException>(() => JsonbConverter.Read(TestWire.ByteSegments(invalid)));
        string invalidUtf16 = new(new[] {(char)0xd800});
        byte[] destination = Enumerable.Repeat((byte)0xcc, 32).ToArray();
        Assert.Throws<System.Text.EncoderFallbackException>(() => JsonbConverter.Write(invalidUtf16, destination));
        Assert.Throws<ArgumentException>(() => JsonbConverter.Write("a\0b", destination));
        Assert.All(destination, b => Assert.Equal((byte)0xcc, b));
    }
}