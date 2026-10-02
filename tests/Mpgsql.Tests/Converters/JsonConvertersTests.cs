using System.Buffers;
using Mpgsql.Converters;
using Mpgsql.Tests.Protocol;

namespace Mpgsql.Tests.Converters;

public sealed class JsonConvertersTests
{
    [Fact]
    public void LiteralPayloadsArraysAndSplitUnicode()
    {
        string value = "{\"x\":\"Я😀\"}";
        ConverterAssertions.CheckScalar(value, "7b2278223a22d0aff09f9880227d", JsonConverter.GetByteCount, JsonConverter.Write,
            JsonConverter.Write, JsonConverter.Read, JsonConverter.Read);
        ConverterAssertions.CheckArray(new string?[] {value, value}, (uint)TypeOid.Json, "7b2278223a22d0aff09f9880227d",
            JsonArrayConverter.GetByteCount, JsonArrayConverter.Write, JsonArrayConverter.Write,
            JsonArrayConverter.Read, JsonArrayConverter.Read, JsonArrayConverter.Read, JsonArrayConverter.Read);
        string?[] nullable = [value, null, ""];
        byte[] payload = new byte[JsonArrayConverter.GetByteCount(nullable)];
        JsonArrayConverter.Write(nullable, payload);
        Assert.Equal(nullable, JsonArrayConverter.Read(payload).ToArray());
        Assert.Equal(nullable, JsonArrayConverter.Read(TestWire.ByteSegments(payload)).ToArray());
        Assert.Null(JsonConverter.ReadNullable((ReadOnlyMemory<byte>?)null));
        Assert.Null(JsonConverter.ReadNullable((ReadOnlySequence<byte>?)null));
        Assert.Equal(0, JsonConverter.Write(null, Span<byte>.Empty));
        byte[] invalid = [0xf0, 0x9f, 0x98];

        Assert.Throws<InvalidDataException>(() => JsonConverter.Read(invalid));
        Assert.Throws<InvalidDataException>(() => JsonConverter.Read(TestWire.ByteSegments(invalid)));
        string invalidUtf16 = new(new[] {(char)0xd800});
        byte[] destination = Enumerable.Repeat((byte)0xcc, 32).ToArray();
        Assert.Throws<System.Text.EncoderFallbackException>(() => JsonConverter.Write(invalidUtf16, destination));
        Assert.Throws<ArgumentException>(() => JsonConverter.Write("a\0b", destination));
        Assert.All(destination, b => Assert.Equal((byte)0xcc, b));
    }
}