using System.Buffers;
using System.Text;
using Mpgsql.Converters;
using Mpgsql.Tests.Protocol;

namespace Mpgsql.Tests.Converters;

public sealed class VariablePayloadEdgeTests
{
    [Theory, InlineData(1), InlineData(2), InlineData(3), InlineData(7), InlineData(16), InlineData(4096)]
    public void LargeUtf8UsesCompleteSegmentsAndSplitScalars(int segmentSize)
    {
        var value = string.Concat(Enumerable.Repeat("ASCII Я ε 中文 😀 e\u0301 ", 512));
        var bytes = Encoding.UTF8.GetBytes(value);
        var input = TestWire.Chunks(Enumerable.Range(0, (bytes.Length + segmentSize - 1) / segmentSize)
            .Select(i => (ReadOnlyMemory<byte>)bytes.AsMemory(i * segmentSize, Math.Min(segmentSize, bytes.Length - i * segmentSize))).ToArray());
        Assert.Equal(value, TextConverter.Read(input));
        Assert.Equal(bytes, TextConverter.ReadUtf8(input).ToArray());
        var result = new byte[bytes.Length + 1];
        JsonbConverter.WriteUtf8(bytes, result);
        Assert.Equal((byte)1, result[0]);
        Assert.Equal(bytes, result[1..]);
    }

    [Theory, InlineData("80"), InlineData("c080"), InlineData("eda080"), InlineData("f4908080"), InlineData("e08080"), InlineData("f09f98")]
    public void InvalidUtf8IsRejectedWhenDecodingAtEveryBoundary(string hex)
    {
        var bytes = TestWire.Bytes(hex);
        Assert.Throws<InvalidDataException>(() => TextConverter.Read(bytes));
        for (var split = 0; split <= bytes.Length; split++)
        {
            var input = TestWire.Chunks(bytes.AsMemory(0, split), ReadOnlyMemory<byte>.Empty, bytes.AsMemory(split));
            Assert.Throws<InvalidDataException>(() => TextConverter.Read(input));
            Assert.Equal(bytes, TextConverter.ReadUtf8(input).ToArray());
        }
    }

    [Theory, InlineData("80"), InlineData("c080"), InlineData("eda080"), InlineData("f4908080"), InlineData("e08080"), InlineData("f09f98"), InlineData("410042")]
    public void RawTextJsonAndXmlPassBytesThroughWithoutContentValidation(string hex)
    {
        var bytes = TestWire.Bytes(hex);
        var output = new byte[bytes.Length + 1];
        Assert.Equal(bytes, TextConverter.ReadUtf8(bytes).ToArray());
        Assert.Equal(bytes, JsonConverter.ReadUtf8(bytes).ToArray());
        Assert.Equal(bytes, XmlConverter.ReadUtf8(bytes).ToArray());
        Assert.Equal(bytes.Length, TextConverter.WriteUtf8(bytes, output));
        Assert.Equal(bytes, output[..bytes.Length]);
        Assert.Equal(bytes.Length, JsonConverter.WriteUtf8(bytes, output));
        Assert.Equal(bytes, output[..bytes.Length]);
        Assert.Equal(bytes.Length, XmlConverter.WriteUtf8(bytes, output));
        Assert.Equal(bytes, output[..bytes.Length]);
        var sequence = TestWire.ByteSegments(bytes);
        Assert.Equal(bytes, JsonConverter.ReadUtf8(sequence).ToArray());
        Assert.Equal(bytes, XmlConverter.ReadUtf8(sequence).ToArray());
    }

    [Fact]
    public void LengthDelimitedStringsDoNotScanForNul()
    {
        const string value = "A\0B";
        byte[] bytes = [65, 0, 66];
        var output = new byte[3];
        Assert.Equal(3, TextConverter.GetByteCount(value));
        Assert.Equal(3, TextConverter.Write(value, output));
        Assert.Equal(bytes, output);
        Assert.Equal(value, TextConverter.Read(output));
        Assert.Equal(value, TextConverter.Read(TestWire.ByteSegments(output)));
        Assert.Equal(3, JsonConverter.Write(value, output));
        Assert.Equal(value, JsonConverter.Read(TestWire.ByteSegments(output)));
        Assert.Equal(3, XmlConverter.Write(value, output));
        Assert.Equal(value, XmlConverter.Read(TestWire.ByteSegments(output)));
    }

    [Fact]
    public void BorrowedByteaAndOwnedByteaHaveExplicitLifetimes()
    {
        byte[] bytes = [0, 1, 128, 255];
        var borrowed = ByteaConverter.ReadBorrowed(bytes.AsMemory());
        var owned = ByteaConverter.Read(bytes);
        bytes[1] = 42;
        Assert.Equal((byte)42, borrowed.Span[1]);
        Assert.Equal((byte)1, owned.Span[1]);
        Assert.True(ByteaConverter.Read(ReadOnlySpan<byte>.Empty).IsEmpty);
        Assert.True(ByteaConverter.ReadNullable(ReadOnlyMemory<byte>.Empty)!.Value.IsEmpty);
        Assert.Null(ByteaConverter.ReadNullable((ReadOnlyMemory<byte>?)null));
        byte[]?[] values = [new byte[] {0, 255}, null, Array.Empty<byte>(), new byte[] {128}];
        var payload = new byte[ByteaArrayConverter.GetByteCount(values)];
        ByteaArrayConverter.Write(values, payload);
        var decoded = ByteaArrayConverter.ReadByteArrays(TestWire.ByteSegments(payload)).ToArray();
        Assert.Equal(values[0], decoded[0]);
        Assert.Null(decoded[1]);
        Assert.NotNull(decoded[2]);
        Assert.Empty(decoded[2]!);
        Assert.Equal(values[3], decoded[3]);
    }

    [Fact]
    public void JsonbRequiresVersionOneEvenWhenSplitIntoEmptySegments()
    {
        foreach (var payload in new[] {Array.Empty<byte>(), new byte[] {0}, new byte[] {2, 123, 125}})
        {
            Assert.Throws<InvalidDataException>(() => JsonbConverter.Read(payload));
            Assert.Throws<InvalidDataException>(() => JsonbConverter.Read(TestWire.Chunks(ReadOnlyMemory<byte>.Empty, payload)));
        }
    }
}