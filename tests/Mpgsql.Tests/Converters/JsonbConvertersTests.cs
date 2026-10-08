using System.Buffers;
using Mpgsql.Converters;
using Mpgsql.Tests.Protocol;

namespace Mpgsql.Tests.Converters;

public sealed class JsonbConvertersTests
{
    [Fact]
    public void LiteralPayloadsArraysAndSplitUnicode()
    {
        Memory<byte> value = "{\"x\":\"Я😀\"}"u8.ToArray();
        ConverterAssertions.CheckScalar(value, "017b2278223a22d0aff09f9880227d", JsonbConverter.GetByteCount, JsonbConverter.Write,
            JsonbConverter.Write, JsonbConverter.Read, JsonbConverter.Read);
        ConverterAssertions.CheckNullableScalar(JsonbConverter.Write, JsonbConverter.Write,
            JsonbConverter.GetByteCount, JsonbConverter.ReadNullable, JsonbConverter.ReadNullable);
        ConverterAssertions.CheckArray(new[] {value, value}, (uint)TypeOid.Jsonb, "017b2278223a22d0aff09f9880227d",
            JsonbArrayConverter.GetByteCount, JsonbArrayConverter.Write, JsonbArrayConverter.Write,
            JsonbArrayConverter.Read, JsonbArrayConverter.Read, JsonbArrayConverter.Read, JsonbArrayConverter.Read);
        ConverterAssertions.CheckArray(new Memory<byte>?[] {value, value}, (uint)TypeOid.Jsonb, "017b2278223a22d0aff09f9880227d",
            NullableJsonbArrayConverter.GetByteCount, NullableJsonbArrayConverter.Write, NullableJsonbArrayConverter.Write,
            NullableJsonbArrayConverter.Read, NullableJsonbArrayConverter.Read, NullableJsonbArrayConverter.Read, NullableJsonbArrayConverter.Read);
        ConverterAssertions.CheckNullableArray<Memory<byte>, JsonbCodec>(value, "017b2278223a22d0aff09f9880227d");
    }

    [Fact]
    public void NullEmptyAndJsonNullRemainDistinct()
    {
        var empty = Memory<byte>.Empty;
        Assert.Equal(1, JsonbConverter.GetByteCount(empty));
        Assert.Equal(1, JsonbConverter.GetByteCount((Memory<byte>?)empty));
        byte[] output = [0xcc, 0xcc];
        Assert.Equal(1, JsonbConverter.Write(empty, output));
        Assert.Equal(new byte[] {1, 0xcc}, output);
        Assert.True(JsonbConverter.Read(new byte[] {1}).IsEmpty);
        Assert.True(JsonbConverter.ReadNullable((ReadOnlyMemory<byte>?)new byte[] {1})!.Value.IsEmpty);
        Assert.True(JsonbConverter.ReadNullable(TestWire.ByteSegments([1]))!.Value.IsEmpty);
        Memory<byte> jsonNull = "null"u8.ToArray();
        ConverterAssertions.CheckScalar(jsonNull, "016e756c6c", JsonbConverter.GetByteCount, JsonbConverter.Write,
            JsonbConverter.Write, JsonbConverter.Read, JsonbConverter.Read);
        Memory<byte>?[] nullable = [jsonNull, null, empty];
        var expected = ConverterAssertions.ArrayBytes((uint)TypeOid.Jsonb, TestWire.Bytes("016e756c6c"), null, [1]);
        var payload = new byte[NullableJsonbArrayConverter.GetByteCount(nullable)];
        NullableJsonbArrayConverter.Write(nullable, payload);
        Assert.Equal(expected, payload);
        var decoded = NullableJsonbArrayConverter.Read(payload);
        Assert.Equal(jsonNull.ToArray(), decoded.Span[0]!.Value.ToArray());
        Assert.Null(decoded.Span[1]);
        Assert.True(decoded.Span[2]!.Value.IsEmpty);
        var scratch = new Memory<byte>?[3];
        Assert.Equal(3, NullableJsonbArrayConverter.Read(payload, scratch));
        NullableJsonbArrayConverter.Write(scratch, payload);
        Assert.Equal(expected, payload);
        Assert.Equal(3, NullableJsonbArrayConverter.Read(TestWire.ByteSegments(payload), scratch));
        NullableJsonbArrayConverter.Write(scratch, payload);
        Assert.Equal(expected, payload);
        Assert.Throws<NotSupportedException>(() => JsonbArrayConverter.Read(payload));
    }

    [Fact]
    public void ReadsReturnOwnedMutableJsonBytesWithoutVersion()
    {
        var payload = TestWire.Bytes("017b2278223a22d0aff09f9880227d");
        var expected = payload[1..];
        var contiguous = JsonbConverter.Read(payload);
        var fragmented = JsonbConverter.Read(TestWire.ByteSegments(payload));
        var borrowed = JsonbConverter.ReadUtf8(payload);
        payload[2] = (byte)'y';
        Assert.Equal(expected, contiguous.ToArray());
        Assert.Equal(expected, fragmented.ToArray());
        Assert.Equal((byte)'y', borrowed[1]);
        contiguous.Span[0] = (byte)'[';
        Assert.Equal((byte)'{', fragmented.Span[0]);
        Assert.Equal((byte)'{', payload[1]);
        var array = ConverterAssertions.ArrayBytes((uint)TypeOid.Jsonb, [1, .. expected], [1, .. expected]);
        var values = JsonbArrayConverter.Read(TestWire.ByteSegments(array));
        array.AsSpan().Clear();
        values.Span[0].Span[0] = (byte)'[';
        Assert.Equal(expected, values.Span[1].ToArray());
    }

    [Theory, InlineData("80"), InlineData("c080"), InlineData("eda080"), InlineData("f4908080"), InlineData("e08080"), InlineData("f09f98"), InlineData("410042")]
    public void RawJsonBytesPassThroughWithoutContentValidation(string hex)
    {
        Memory<byte> value = TestWire.Bytes(hex);
        var destination = Enumerable.Repeat((byte)0xcc, 64).ToArray();
        byte[] payload = [1, .. value.Span];
        Assert.Equal(payload.Length, JsonbConverter.GetByteCount(value));
        Assert.Equal(payload.Length, JsonbConverter.Write(value, destination));
        Assert.Equal(payload, destination[..payload.Length]);
        Assert.All(destination[payload.Length..], b => Assert.Equal((byte)0xcc, b));
        var writer = new ArrayBufferWriter<byte>();
        JsonbConverter.Write(value, writer);
        Assert.Equal(payload, writer.WrittenSpan.ToArray());
        Assert.Equal(value.ToArray(), JsonbConverter.Read(payload).ToArray());
        Assert.Equal(value.ToArray(), JsonbConverter.ReadUtf8(payload).ToArray());
        for (var split = 0; split <= payload.Length; split++)
        {
            var input = TestWire.Chunks(payload.AsMemory(0, split), ReadOnlyMemory<byte>.Empty, payload.AsMemory(split));
            Assert.Equal(value.ToArray(), JsonbConverter.Read(input).ToArray());
            Assert.Equal(value.ToArray(), JsonbConverter.ReadUtf8(input).ToArray());
        }
        Memory<byte>[] values = ["null"u8.ToArray(), value];
        var array = new byte[JsonbArrayConverter.GetByteCount(values)];
        JsonbArrayConverter.Write(values, array);
        Assert.Equal(value.ToArray(), JsonbArrayConverter.Read(TestWire.ByteSegments(array)).Span[1].ToArray());
        Memory<byte>?[] nullable = [null, value];
        array = new byte[NullableJsonbArrayConverter.GetByteCount(nullable)];
        NullableJsonbArrayConverter.Write(nullable, array);
        Assert.Equal(value.ToArray(), NullableJsonbArrayConverter.Read(TestWire.ByteSegments(array)).Span[1]!.Value.ToArray());
    }

    [Fact]
    public void CapacityAndOverlapFailuresLeaveDestinationUnchanged()
    {
        var storage = Enumerable.Repeat((byte)0xcc, 64).ToArray();
        "null"u8.CopyTo(storage.AsSpan(12));
        var value = storage.AsMemory(12, 4);
        var original = storage.ToArray();
        Assert.Throws<ArgumentException>(() => JsonbConverter.Write(value, storage.AsSpan(0, 4)));
        Assert.Throws<ArgumentException>(() => JsonbConverter.Write(value, storage.AsSpan(10, 5)));
        Assert.Throws<ArgumentException>(() => JsonbArrayConverter.Write(new[] {value}, storage));
        Assert.Throws<ArgumentException>(() => NullableJsonbArrayConverter.Write(new Memory<byte>?[] {null, value}, storage));
        var writer = new AliasedWriter(storage.AsMemory(10));
        Assert.Throws<ArgumentException>(() => JsonbConverter.Write(value, writer));
        Assert.Throws<ArgumentException>(() => JsonbArrayConverter.Write(new[] {value}, writer));
        Assert.Throws<ArgumentException>(() => NullableJsonbArrayConverter.Write(new Memory<byte>?[] {null, value}, writer));
        Assert.Equal(0, writer.Advanced);
        Assert.Equal(original, storage);
    }

    private sealed class AliasedWriter(Memory<byte> storage) : IBufferWriter<byte>
    {
        internal int Advanced { get; private set; }
        public void Advance(int count)
        {
            Advanced += count;
        }
        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            return storage;
        }
        public Span<byte> GetSpan(int sizeHint = 0)
        {
            return storage.Span;
        }
    }
}