using System.Buffers;
using System.Text;
using Mpgsql.Converters;
using Mpgsql.Tests.Protocol;

namespace Mpgsql.Tests.Converters;

public sealed class VariablePayloadEdgeTests
{
    [Theory]
    [InlineData(1), InlineData(2), InlineData(3), InlineData(7), InlineData(16), InlineData(4096)]
    public void LargeUtf8UsesCompleteSegmentsAndSplitScalars(int segmentSize)
    {
        string value = string.Concat(Enumerable.Repeat("ASCII Я ε 中文 😀 e\u0301 ", 512));
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        var input = TestWire.Chunks(Enumerable.Range(0, (bytes.Length + segmentSize - 1) / segmentSize)
            .Select(i => (ReadOnlyMemory<byte>)bytes.AsMemory(i * segmentSize, Math.Min(segmentSize, bytes.Length - i * segmentSize))).ToArray());
        Assert.Equal(value, TextConverter.Read(input));
        Assert.Equal(bytes, TextConverter.ReadUtf8(input).ToArray());
        byte[] result = new byte[bytes.Length + 1];
        JsonbConverter.WriteUtf8(bytes, result);
        Assert.Equal((byte)1, result[0]);
        Assert.Equal(bytes, result[1..]);
    }

    [Theory]
    [InlineData("80"), InlineData("c080"), InlineData("eda080"), InlineData("f4908080"), InlineData("e08080"), InlineData("f09f98"), InlineData("410042")]
    public void InvalidUtf8IsRejectedAtEveryBoundary(string hex)
    {
        byte[] bytes = TestWire.Bytes(hex);
        Assert.Throws<InvalidDataException>(() => TextConverter.Read(bytes));
        for (int split = 0; split <= bytes.Length; split++)
        {
            var input = TestWire.Chunks(bytes.AsMemory(0, split), ReadOnlyMemory<byte>.Empty, bytes.AsMemory(split));
            Assert.Throws<InvalidDataException>(() => TextConverter.Read(input));
            Assert.Throws<InvalidDataException>(() => TextConverter.ReadUtf8(input));
        }
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
        Assert.True(ByteaConverter.ReadNullable((ReadOnlyMemory<byte>?)ReadOnlyMemory<byte>.Empty)!.Value.IsEmpty);
        Assert.Null(ByteaConverter.ReadNullable((ReadOnlyMemory<byte>?)null));
        byte[]?[] values = [new byte[] {0, 255}, null, Array.Empty<byte>(), new byte[] {128}];
        byte[] payload = new byte[ByteaArrayConverter.GetByteCount(values)];
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
        foreach (byte[] payload in new[] {Array.Empty<byte>(), new byte[] {0}, new byte[] {2, 123, 125}})
        {
            Assert.Throws<InvalidDataException>(() => JsonbConverter.Read(payload));
            Assert.Throws<InvalidDataException>(() => JsonbConverter.Read(TestWire.Chunks(ReadOnlyMemory<byte>.Empty, payload)));
        }
    }
}