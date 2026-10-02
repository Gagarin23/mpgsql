using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Mpgsql.Converters;
using Mpgsql.Protocol;
using Mpgsql.Tests.Protocol;

namespace Mpgsql.Tests.Converters;

public sealed class NullableInt64ArrayConverterTests
{
    public static TheoryData<long?[], string> Payloads => new()
    {
        { [], "00000000 00000000 00000014" },
        { [null], "00000001 00000001 00000014 00000001 00000001 ffffffff" },
        { [0x0102030405060708], "00000001 00000000 00000014 00000001 00000001 00000008 0102030405060708" },
        { [null, long.MinValue, -1, null, 0, 1, long.MaxValue, null],
            "00000001 00000001 00000014 00000008 00000001 " +
            "ffffffff 00000008 8000000000000000 00000008 ffffffffffffffff ffffffff " +
            "00000008 0000000000000000 00000008 0000000000000001 00000008 7fffffffffffffff ffffffff" }
    };

    [Theory, MemberData(nameof(Payloads))]
    public void WritesAndReadsLiteralPayload(long?[] values, string hex)
    {
        byte[] expected = TestWire.Bytes(hex);
        Assert.Equal(expected.Length, NullableInt64ArrayConverter.GetByteCount(values));
        Assert.Equal(expected.Length, NullableInt64ArrayConverter.GetByteCount(values.Length, values.Count(v => !v.HasValue)));
        byte[] output = Enumerable.Repeat((byte)0xcc, expected.Length + 8).ToArray();
        Assert.Equal(expected.Length, NullableInt64ArrayConverter.Write(values, output.AsSpan(3)));
        Assert.Equal(expected, output.AsSpan(3, expected.Length).ToArray());
        Assert.All(output[..3].Concat(output[(3 + expected.Length)..]), v => Assert.Equal((byte)0xcc, v));
        Assert.Equal(values, NullableInt64ArrayConverter.Read(expected.AsSpan()).ToArray());
        Assert.Equal(values, NullableInt64ArrayConverter.Read(TestWire.ByteSegments(expected)).ToArray());

        var writer = new RecordingWriter(expected.Length);
        NullableInt64ArrayConverter.Write(values, writer);
        Assert.Equal(expected.Length, writer.SizeHint);
        Assert.Equal(expected.Length, writer.Advanced);
        Assert.Equal(1, writer.Reservations);
        Assert.Equal(1, writer.Advances);
        Assert.Equal(expected, writer.Bytes[..expected.Length]);
        Assert.All(writer.Bytes[expected.Length..], v => Assert.Equal((byte)0xcc, v));

        long?[] reused = Enumerable.Repeat<long?>(42, values.Length + 4).ToArray();
        Assert.Equal(values.Length, NullableInt64ArrayConverter.Read(expected.AsSpan(), reused.AsSpan(2)));
        Assert.Equal(values, reused.AsSpan(2, values.Length).ToArray());
        Assert.Equal(new long?[] { 42, 42 }, reused[..2]);
        Assert.Equal(new long?[] { 42, 42 }, reused[(values.Length + 2)..]);
        Assert.Equal(values.Length, NullableInt64ArrayConverter.Read(TestWire.ByteSegments(expected), reused.AsSpan(2)));
        Assert.Equal(values, reused.AsSpan(2, values.Length).ToArray());
    }

    [Theory]
    [InlineData(0), InlineData(1), InlineData(3), InlineData(4), InlineData(5), InlineData(8)]
    [InlineData(255), InlineData(256), InlineData(257), InlineData(4095), InlineData(4096), InlineData(4097), InlineData(65536)]
    public void MixedNullPatternsAndMemorySlicesRoundTrip(int count)
    {
        foreach (int period in new[] { 0, 1, 2, 7 })
        {
            long?[] backing = new long?[count + 2];
            backing[0] = 99; backing[^1] = 100;
            for (int i = 0; i < count; i++)
                backing[i + 1] = period != 0 && i % period == 0 ? null : unchecked(long.MinValue + i);
            var before = backing.ToArray();
            var values = backing.AsMemory(1, count);
            byte[] bytes = Encode(values);
            Assert.Equal(before, backing);
            Assert.Equal(before[1..^1], NullableInt64ArrayConverter.Read(bytes.AsSpan()).ToArray());
            var chunks = Enumerable.Range(0, (bytes.Length + 4095) / 4096)
                .Select(i => (ReadOnlyMemory<byte>)bytes.AsMemory(i * 4096, Math.Min(4096, bytes.Length - i * 4096))).ToArray();
            var sequence = TestWire.Chunks(chunks);
            var storage = Enumerable.Repeat<long?>(42, count).ToArray();
            Assert.Equal(count, NullableInt64ArrayConverter.Read(sequence, storage));
            Assert.Equal(before[1..^1], storage);
            Assert.Equal(before[1..^1], NullableInt64ArrayConverter.Read(sequence).ToArray());
        }
    }

    [Fact]
    public void EverySplitAndEmptySegmentAreAccepted()
    {
        long?[] values = [null, long.MinValue, null, 0x0102030405060708, null, long.MaxValue, null];
        byte[] bytes = Encode(values);
        for (int split = 0; split <= bytes.Length; split++)
        {
            var sequence = TestWire.Chunks(ReadOnlyMemory<byte>.Empty, bytes.AsMemory(0, split),
                ReadOnlyMemory<byte>.Empty, bytes.AsMemory(split), ReadOnlyMemory<byte>.Empty);
            Assert.Equal(values, NullableInt64ArrayConverter.Read(sequence).ToArray());
            var reused = Enumerable.Repeat<long?>(42, values.Length).ToArray();
            Assert.Equal(values.Length, NullableInt64ArrayConverter.Read(sequence, reused));
            Assert.Equal(values, reused);
        }
    }

    [Fact]
    public void EveryTruncationAndTrailingBytesAreRejected()
    {
        byte[] bytes = Encode(new long?[] { null, 42, null, long.MinValue, null });
        for (int length = 0; length < bytes.Length; length++) AssertInvalid(bytes[..length]);
        AssertInvalid([..bytes, 0]);
        // Trailing bytes can fall within the min/max size envelope, but must still be rejected.
        AssertInvalid(TestWire.Bytes("00000001 00000001 00000014 00000001 00000001 ffffffff 0000000000000000"));
        AssertInvalid(TestWire.Bytes("00000000 00000000 00000014 00"));
        AssertInvalid(TestWire.Bytes("00000001 00000001 00000014 00000002 00000001 ffffffff fffffffe"));
    }

    [Theory]
    [InlineData(0, -1), InlineData(0, 7), InlineData(4, -1), InlineData(4, 2)]
    [InlineData(8, 23), InlineData(8, 1016), InlineData(12, -1), InlineData(12, int.MaxValue)]
    [InlineData(16, int.MaxValue)]
    public void InvalidHeaderIsRejected(int offset, int value)
    {
        byte[] bytes = Encode(new long?[] { null, 42, null });
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(offset), value);
        AssertInvalid(bytes);
    }

    [Theory]
    [InlineData(-2), InlineData(0), InlineData(4), InlineData(7), InlineData(9), InlineData(int.MaxValue)]
    public void InvalidElementLengthsAreRejected(int length)
    {
        byte[] bytes = Encode(new long?[] { null, 42, null });
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(24), length);
        AssertInvalid(bytes);
    }

    [Theory]
    [InlineData(int.MinValue), InlineData(-2), InlineData(0), InlineData(1), InlineData(int.MaxValue - 3)]
    public void LowerBoundsAreNormalized(int lowerBound)
    {
        long?[] values = [null, long.MinValue, long.MaxValue];
        byte[] bytes = Encode(values);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), lowerBound);
        Assert.Equal(values, NullableInt64ArrayConverter.Read(bytes.AsSpan()).ToArray());
        Assert.Equal(values, NullableInt64ArrayConverter.Read(TestWire.ByteSegments(bytes)).ToArray());
    }

    [Fact]
    public void NullLengthsAreAuthoritativeAndRetainedNullFlagIsAccepted()
    {
        foreach (long?[] values in new long?[][] { [1, 2], [null, 2] })
            foreach (int flags in new[] { 0, 1 })
            {
                byte[] bytes = Encode(values);
                BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4), flags);
                Assert.Equal(values, NullableInt64ArrayConverter.Read(bytes.AsSpan()).ToArray());
                Assert.Equal(values, NullableInt64ArrayConverter.Read(TestWire.ByteSegments(bytes)).ToArray());
            }
    }

    [Fact]
    public void DefaultAndOneDimensionalEmptyArraysAreAccepted()
    {
        byte[] bytes = new byte[12];
        NullableInt64ArrayConverter.Write(default, bytes);
        Assert.Equal(TestWire.Bytes("00000000 00000000 00000014"), bytes);
        byte[] dimensional = TestWire.Bytes("00000001 00000001 00000014 00000000 fffffffe");
        Assert.True(NullableInt64ArrayConverter.Read(dimensional.AsSpan()).IsEmpty);
        Assert.True(NullableInt64ArrayConverter.Read(TestWire.ByteSegments(dimensional)).IsEmpty);
        Assert.Equal(0, NullableInt64ArrayConverter.Read(dimensional.AsSpan(), Span<long?>.Empty));
    }

    [Fact]
    public void BoundsChecksPrecedeMutationAndSizeArithmeticIsChecked()
    {
        long?[] values = [null, 42];
        byte[] bytes = Encode(values);
        byte[] destination = Enumerable.Repeat((byte)0xcc, bytes.Length - 1).ToArray();
        Assert.Throws<ArgumentException>(() => NullableInt64ArrayConverter.Write(values, destination));
        Assert.All(destination, v => Assert.Equal((byte)0xcc, v));
        long?[] reused = [99];
        Assert.Throws<ArgumentException>(() => NullableInt64ArrayConverter.Read(bytes.AsSpan(), reused));
        Assert.Throws<ArgumentException>(() => NullableInt64ArrayConverter.Read(TestWire.ByteSegments(bytes), reused));
        Assert.Equal(99, reused[0]);
        Assert.Throws<ArgumentNullException>(() => NullableInt64ArrayConverter.Write(values, (IBufferWriter<byte>)null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => NullableInt64ArrayConverter.GetByteCount(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => NullableInt64ArrayConverter.GetByteCount(1, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => NullableInt64ArrayConverter.GetByteCount(1, 2));
        Assert.Throws<OverflowException>(() => NullableInt64ArrayConverter.GetByteCount(int.MaxValue, int.MaxValue));
        Assert.Throws<OverflowException>(() => NullableInt64ArrayConverter.GetByteCount(int.MaxValue, 0));
        int largestAllNull = (int.MaxValue - 20) / 4;
        Assert.Equal(20 + 4 * largestAllNull, NullableInt64ArrayConverter.GetByteCount(largestAllNull, largestAllNull));
        Assert.Throws<OverflowException>(() => NullableInt64ArrayConverter.GetByteCount(largestAllNull + 1, largestAllNull + 1));
    }

    [Fact]
    public void OverlapIsRejectedBeforeMutation()
    {
        long?[] values = [42, null, 99, null, 1, 2, 3, 4];
        var before = values.ToArray();
        Assert.Throws<ArgumentException>(() => NullableInt64ArrayConverter.Write(values.AsMemory(0, 2), Bytes(values)));
        Assert.Equal(before, values);
        byte[] payload = Encode(new long?[] { null, 42 });
        payload.CopyTo(Bytes(values));
        byte[] original = Bytes(values).ToArray();
        Assert.Throws<ArgumentException>(() => NullableInt64ArrayConverter.Read(Bytes(values)[..payload.Length], values.AsSpan(0, 2)));
        var memory = new ByteView(values);
        var sequence = TestWire.Chunks(memory.Memory[..24], memory.Memory.Slice(24, payload.Length - 24));
        Assert.Throws<ArgumentException>(() => NullableInt64ArrayConverter.Read(sequence, values.AsSpan(1, 2)));
        Assert.Equal(original, Bytes(values).ToArray());
    }

    [Fact]
    public void OwnedResultDoesNotBorrowBytesAndMultidimensionalArraysAreRejected()
    {
        long?[] values = [null, 42];
        byte[] bytes = Encode(values);
        var result = NullableInt64ArrayConverter.Read(TestWire.ByteSegments(bytes));
        bytes.AsSpan().Clear();
        Assert.Equal(values, result.ToArray());
        byte[] matrix = TestWire.Bytes("00000002 00000001 00000014 00000001 00000001 00000001 00000001 ffffffff");
        Assert.Throws<NotSupportedException>(() => NullableInt64ArrayConverter.Read(matrix.AsSpan()));
        Assert.Throws<NotSupportedException>(() => NullableInt64ArrayConverter.Read(TestWire.ByteSegments(matrix)));
    }

    [Fact]
    public void BindAndDataRowKeepOuterNullSeparateFromNullElements()
    {
        byte[] payload = Encode(new long?[] { null });
        var bind = FrontendMessage.Bind(parameters: new ReadOnlyMemory<byte>?[] { payload, null },
            parameterFormats: new[] { FormatCode.Binary }, resultFormats: new[] { FormatCode.Binary });
        byte[] packet = new byte[bind.GetByteCount()];
        bind.Write(packet);
        Assert.Equal(TestWire.Bytes("42 00000030 00 00 0001 0001 0002 00000018 " +
            "00000001 00000001 00000014 00000001 00000001 ffffffff ffffffff 0001 0001"), packet);
        var input = TestWire.ByteSegments(TestWire.Bytes("44 00000026 0002 00000018 " +
            "00000001 00000001 00000014 00000001 00000001 ffffffff ffffffff"));
        var fields = new ReadOnlySequence<byte>?[2];
        Assert.True(BackendMessageReader.TryRead(ref input, fields, out _, out var row));
        Assert.Equal(new long?[] { null }, NullableInt64ArrayConverter.Read(row.Values.Span[0]!.Value).ToArray());
        Assert.False(row.Values.Span[1].HasValue);
        Assert.True(input.IsEmpty);
    }

    private static void AssertInvalid(byte[] bytes)
    {
        Assert.Throws<InvalidDataException>(() => NullableInt64ArrayConverter.Read(bytes.AsSpan()));
        Assert.Throws<InvalidDataException>(() => NullableInt64ArrayConverter.Read(TestWire.ByteSegments(bytes)));
        Assert.Throws<InvalidDataException>(() => NullableInt64ArrayConverter.Read(bytes.AsSpan(), new long?[8]));
        Assert.Throws<InvalidDataException>(() => NullableInt64ArrayConverter.Read(TestWire.ByteSegments(bytes), new long?[8]));
    }

    private static byte[] Encode(ReadOnlyMemory<long?> values)
    {
        byte[] bytes = new byte[NullableInt64ArrayConverter.GetByteCount(values)];
        NullableInt64ArrayConverter.Write(values, bytes);
        return bytes;
    }

    private static Span<byte> Bytes(long?[] values) => MemoryMarshal.CreateSpan(
        ref Unsafe.As<long?, byte>(ref MemoryMarshal.GetArrayDataReference(values)), values.Length * Unsafe.SizeOf<long?>());

    private sealed class ByteView(long?[] values) : MemoryManager<byte>
    {
        public override Span<byte> GetSpan() => Bytes(values);
        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }

    private sealed class RecordingWriter(int size) : IBufferWriter<byte>
    {
        internal byte[] Bytes { get; } = Enumerable.Repeat((byte)0xcc, size + 16).ToArray();
        internal int SizeHint, Advanced, Reservations, Advances;
        public Span<byte> GetSpan(int sizeHint = 0) { SizeHint = sizeHint; Reservations++; return Bytes; }
        public Memory<byte> GetMemory(int sizeHint = 0) => throw new NotSupportedException();
        public void Advance(int count) { Advanced = count; Advances++; }
    }
}
