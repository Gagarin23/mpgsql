using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Mpgsql.Converters;
using Mpgsql.Protocol;
using Mpgsql.Tests.Protocol;

namespace Mpgsql.Tests.Converters;

public sealed class Int64ArrayConverterTests
{
    // Array payload vectors exclude the outer Bind/DataRow field length.
    public static TheoryData<long[], string> Payloads => new()
    {
        {[], "00000000 00000000 00000014"},
        {[0x0102030405060708], "00000001 00000000 00000014 00000001 00000001 00000008 0102030405060708"},
        {
            [long.MinValue, -1, 0, 1, long.MaxValue],
            "00000001 00000000 00000014 00000005 00000001 " +
            "00000008 8000000000000000 00000008 ffffffffffffffff 00000008 0000000000000000 " +
            "00000008 0000000000000001 00000008 7fffffffffffffff"
        }
    };

    [Theory, MemberData(nameof(Payloads))]
    public void WritesAndReadsLiteralBigEndianPayload(long[] values,
        string hex)
    {
        byte[] expected = TestWire.Bytes(hex);
        Assert.Equal(expected.Length,
            Int64ArrayConverter.GetByteCount(values));
        Assert.Equal(expected.Length,
            Int64ArrayConverter.GetByteCount(values.Length));
        byte[] bytes =
        [
            .. Enumerable.Repeat((byte)0xcc,
                expected.Length + 10)
        ];
        Assert.Equal(expected.Length,
            Int64ArrayConverter.Write(values,
                bytes.AsSpan(3)));
        Assert.Equal(expected,
            bytes.AsSpan(3,
                    expected.Length)
                .ToArray());
        Assert.All(bytes[..3]
                .Concat(bytes[(3 + expected.Length)..]),
            value => Assert.Equal((byte)0xcc,
                value));
        Assert.Equal(values,
            Int64ArrayConverter.Read(expected.AsSpan())
                .ToArray());
        Assert.Equal(values,
            Int64ArrayConverter.Read(TestWire.ByteSegments(expected))
                .ToArray());

        var writer = new RecordingWriter(expected.Length);
        Int64ArrayConverter.Write(values,
            writer);
        Assert.Equal(expected.Length,
            writer.SizeHint);
        Assert.Equal(expected.Length,
            writer.Advanced);
        Assert.Equal(1,
            writer.GetSpanCalls);
        Assert.Equal(1,
            writer.AdvanceCalls);
        Assert.Equal(expected,
            writer.Bytes[..expected.Length]);
        Assert.All(writer.Bytes[expected.Length..],
            value => Assert.Equal((byte)0xcc,
                value));
    }

    [Fact]
    public void DefaultMemoryIsAnEmptyArrayAndOneDimensionalEmptyIsAccepted()
    {
        byte[] bytes = new byte[12];
        Int64ArrayConverter.Write(default,
            bytes);
        Assert.Equal(TestWire.Bytes("00000000 00000000 00000014"),
            bytes);
        byte[] dimensional = TestWire.Bytes("00000001 00000000 00000014 00000000 fffffffe");
        Assert.True(Int64ArrayConverter.Read(dimensional.AsSpan()).IsEmpty);
        Assert.True(Int64ArrayConverter.Read(TestWire.ByteSegments(dimensional)).IsEmpty);
        Assert.Equal(0,
            Int64ArrayConverter.Read(dimensional.AsSpan(),
                Span<long>.Empty));
    }

    [Fact]
    public void MemorySliceIsRespectedWithoutChangingInput()
    {
        long[] backing = [42, long.MinValue, 0x0102030405060708, -1, long.MaxValue, 99];
        var before = backing.ToArray();
        ReadOnlyMemory<long> slice = backing.AsMemory(1,
            4);
        byte[] bytes = Encode(slice);
        Assert.Equal(before,
            backing);
        Assert.Equal(before[1..5],
            Int64ArrayConverter.Read(bytes.AsSpan())
                .ToArray());
    }

    [Theory]
    [InlineData(0), InlineData(1), InlineData(2), InlineData(3), InlineData(4), InlineData(5)]
    [InlineData(7), InlineData(8), InlineData(9), InlineData(15), InlineData(16), InlineData(17)]
    [InlineData(31), InlineData(32), InlineData(33), InlineData(255), InlineData(256), InlineData(257)]
    [InlineData(4095), InlineData(4096), InlineData(4097), InlineData(65536)]
    public void MatchesIndependentScalarEncodingAndReusesStorage(int count)
    {
        long[] values = MakeValues(count);
        byte[] expected = ScalarEncode(values);
        Assert.Equal(expected,
            Encode(values));
        var storage = Enumerable.Repeat(42L,
            count + 4).ToArray();
        Assert.Equal(count,
            Int64ArrayConverter.Read(expected.AsSpan(),
                storage.AsSpan(2)));
        Assert.Equal(values,
            storage.AsSpan(2,
                    count)
                .ToArray());
        Assert.Equal(new long[]
            {
                42,
                42
            },
            storage[..2]);
        Assert.Equal(new long[]
            {
                42,
                42
            },
            storage[(count + 2)..]);
        Assert.Equal(values,
            Int64ArrayConverter.Read(expected.AsSpan())
                .ToArray());
        var chunks = Enumerable.Range(0,
                (expected.Length + 4095) / 4096)
            .Select(i => (ReadOnlyMemory<byte>)expected.AsMemory(i * 4096,
                Math.Min(4096,
                    expected.Length - i * 4096))).ToArray();
        var sequence = TestWire.Chunks(chunks);
        Assert.Equal(count,
            Int64ArrayConverter.Read(sequence,
                storage.AsSpan(2)));
        Assert.Equal(values,
            storage.AsSpan(2,
                    count)
                .ToArray());
        Assert.Equal(values,
            Int64ArrayConverter.Read(sequence)
                .ToArray());
    }

    [Fact]
    public void EveryPossibleSplitAndEmptySegmentsAreAccepted()
    {
        long[] values = MakeValues(9);
        byte[] bytes = ScalarEncode(values);
        for (int split = 0; split <= bytes.Length; split++)
        {
            var sequence = TestWire.Chunks(ReadOnlyMemory<byte>.Empty,
                bytes.AsMemory(0,
                    split),
                ReadOnlyMemory<byte>.Empty,
                bytes.AsMemory(split),
                ReadOnlyMemory<byte>.Empty);
            Assert.Equal(values,
                Int64ArrayConverter.Read(sequence)
                    .ToArray());
            var storage = new long[values.Length];
            Assert.Equal(values.Length,
                Int64ArrayConverter.Read(sequence,
                    storage));
            Assert.Equal(values,
                storage);
        }
        var singleBytes = TestWire.ByteSegments(bytes);
        var output = new long[values.Length];
        Assert.Equal(values.Length,
            Int64ArrayConverter.Read(singleBytes,
                output));
        Assert.Equal(values,
            output);
    }

    [Theory]
    [InlineData(int.MinValue), InlineData(-2), InlineData(0), InlineData(1), InlineData(int.MaxValue - 4)]
    public void LowerBoundsAreNormalized(int lowerBound)
    {
        long[] values = [1, 2, 3, 4];
        byte[] bytes = ScalarEncode(values);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16),
            lowerBound);
        Assert.Equal(values,
            Int64ArrayConverter.Read(bytes.AsSpan())
                .ToArray());
        Assert.Equal(values,
            Int64ArrayConverter.Read(TestWire.ByteSegments(bytes))
                .ToArray());
    }

    [Fact]
    public void ReadResultOutlivesInputAndOtherResults()
    {
        byte[] input = ScalarEncode([1, 2, 3, 4, 5]);
        ReadOnlyMemory<long> first = Int64ArrayConverter.Read(input.AsSpan());
        ReadOnlyMemory<long> second = Int64ArrayConverter.Read(TestWire.ByteSegments(input));
        input.AsSpan().Fill(0);
        Assert.Equal(new long[]
            {
                1,
                2,
                3,
                4,
                5
            },
            first.ToArray());
        Assert.Equal(first.ToArray(),
            second.ToArray());
        Assert.True(MemoryMarshal.TryGetArray(first,
            out var firstArray));
        Assert.True(MemoryMarshal.TryGetArray(second,
            out var secondArray));
        Assert.NotSame(firstArray.Array,
            secondArray.Array);
    }

    [Fact]
    public void CapacityErrorsDoNotModifyStorage()
    {
        long[] values = [1, 2, 3, 4];
        byte[] shortOutput =
        [
            .. Enumerable.Repeat((byte)42,
                Int64ArrayConverter.GetByteCount(values) - 1)
        ];
        Assert.Throws<ArgumentException>(() => Int64ArrayConverter.Write(values,
            shortOutput));
        Assert.All(shortOutput,
            value => Assert.Equal((byte)42,
                value));
        byte[] payload = ScalarEncode(values);
        long[] shortStorage = [42, 42, 42];
        Assert.Throws<ArgumentException>(() => Int64ArrayConverter.Read(payload.AsSpan(),
            shortStorage));
        Assert.Throws<ArgumentException>(() => Int64ArrayConverter.Read(TestWire.ByteSegments(payload),
            shortStorage));
        Assert.Equal(new long[]
            {
                42,
                42,
                42
            },
            shortStorage);
        Assert.Throws<ArgumentNullException>(() => Int64ArrayConverter.Write(values,
            (IBufferWriter<byte>)null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => Int64ArrayConverter.GetByteCount(-1));
        Assert.Throws<OverflowException>(() => Int64ArrayConverter.GetByteCount(int.MaxValue));
        int maximumCount = (int.MaxValue - 20) / 12;
        Assert.Equal(20 + maximumCount * 12,
            Int64ArrayConverter.GetByteCount(maximumCount));
        Assert.Throws<OverflowException>(() => Int64ArrayConverter.GetByteCount(maximumCount + 1));
    }

    [Fact]
    public void TruncatedOrTrailingPayloadIsRejectedBeforeWritingStorage()
    {
        byte[] bytes = ScalarEncode(MakeValues(9));
        for (int length = 0; length < bytes.Length; length++)
        {
            byte[] incomplete = bytes[..length];
            AssertInvalid(incomplete);
        }
        AssertInvalid([.. bytes, 0]);
        AssertInvalid(TestWire.Bytes("00000000 00000000 00000014 00"));
        AssertInvalid(TestWire.Bytes("00000000 00000000 00000014 00000000 00000001"));
    }

    [Theory]
    [InlineData(0, -1), InlineData(0, 7), InlineData(4, -1), InlineData(4, 2)]
    [InlineData(8, 23), InlineData(8, 1016), InlineData(12, -1), InlineData(12, int.MaxValue)]
    [InlineData(16, int.MaxValue)]
    public void InvalidHeaderIsRejected(int offset,
        int value)
    {
        byte[] bytes = ScalarEncode(MakeValues(9));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(offset),
            value);
        AssertInvalid(bytes);
    }

    [Theory]
    [InlineData(0), InlineData(1), InlineData(2), InlineData(3), InlineData(4)]
    [InlineData(5), InlineData(6), InlineData(7), InlineData(8)]
    public void EveryScalarAndVectorRecordPrefixIsValidated(int index)
    {
        foreach (int length in new[] {-2, -1, 0, 4, 7, 9, int.MaxValue})
        {
            byte[] bytes = ScalarEncode(MakeValues(9));
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20 + index * 12),
                length);
            Assert.Throws<InvalidDataException>(() => Int64ArrayConverter.Read(bytes.AsSpan()));
            Assert.Throws<InvalidDataException>(() => Int64ArrayConverter.Read(TestWire.ByteSegments(bytes)));
            // Split inside a later SIMD block as well as before it.
            var sequence = TestWire.Chunks(bytes.AsMemory(0,
                    75),
                bytes.AsMemory(75));
            Assert.Throws<InvalidDataException>(() => Int64ArrayConverter.Read(sequence,
                new long[9]));
        }
    }

    [Fact]
    public void SqlNullElementsAndMultidimensionalArraysAreExplicitlyUnsupported()
    {
        byte[] nullArray = TestWire.Bytes("00000001 00000001 00000014 00000001 00000001 ffffffff");
        byte[] matrix = TestWire.Bytes("00000002 00000000 00000014 00000001 00000001 00000001 00000001 00000008 000000000000002a");
        foreach (byte[] bytes in new[] {nullArray, matrix})
        {
            Assert.Throws<NotSupportedException>(() => Int64ArrayConverter.Read(bytes.AsSpan()));
            Assert.Throws<NotSupportedException>(() => Int64ArrayConverter.Read(TestWire.ByteSegments(bytes)));
            long[] storage = [42];
            Assert.Throws<NotSupportedException>(() => Int64ArrayConverter.Read(bytes.AsSpan(),
                storage));
            Assert.Equal(42,
                storage[0]);
        }
    }

    [Theory]
    [InlineData(0), InlineData(1), InlineData(4), InlineData(9)]
    public void NullableFlagWithoutActualNullElementsIsAccepted(int count)
    {
        long[] values = MakeValues(count);
        byte[] bytes = ScalarEncode(values);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4),
            1);
        Assert.Equal(values,
            Int64ArrayConverter.Read(bytes.AsSpan())
                .ToArray());
        Assert.Equal(values,
            Int64ArrayConverter.Read(TestWire.ByteSegments(bytes))
                .ToArray());
        var storage = new long[count];
        Assert.Equal(count,
            Int64ArrayConverter.Read(bytes.AsSpan(),
                storage));
        Assert.Equal(values,
            storage);
        Assert.Equal(count,
            Int64ArrayConverter.Read(TestWire.ByteSegments(bytes),
                storage));
        Assert.Equal(values,
            storage);
    }

    [Fact]
    public void ActualNullsInFlaggedArrayAreRejectedBeforeChangingStorage()
    {
        byte[] original = ScalarEncode(MakeValues(9));
        for (int index = 0; index < 9; index++)
        {
            int offset = 20 + index * 12;
            byte[] bytes =
            [
                .. original.AsSpan(0,
                    offset),
                0xff, 0xff, 0xff, 0xff, .. original.AsSpan(offset + 12)
            ];
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4),
                1);
            long[] storage =
            [
                .. Enumerable.Repeat(42L,
                    9)
            ];
            Assert.Throws<NotSupportedException>(() => Int64ArrayConverter.Read(bytes.AsSpan()));
            Assert.Throws<NotSupportedException>(() => Int64ArrayConverter.Read(TestWire.ByteSegments(bytes)));
            Assert.Throws<NotSupportedException>(() => Int64ArrayConverter.Read(bytes.AsSpan(),
                storage));
            Assert.Throws<NotSupportedException>(() => Int64ArrayConverter.Read(TestWire.ByteSegments(bytes),
                storage));
            Assert.All(storage,
                value => Assert.Equal(42,
                    value));
        }
        byte[] invalid = ScalarEncode(MakeValues(9));
        BinaryPrimitives.WriteInt32BigEndian(invalid.AsSpan(4),
            1);
        BinaryPrimitives.WriteInt32BigEndian(invalid.AsSpan(20),
            4);
        AssertInvalid(invalid);
    }

    [Fact]
    public void OverlappingStorageIsRejectedBeforeMutation()
    {
        long[] backing = new long[10];
        backing[0] = 42;
        var before = backing.ToArray();
        Assert.Throws<ArgumentException>(() => Int64ArrayConverter.Write(backing.AsMemory(0,
                4),
            MemoryMarshal.AsBytes(backing.AsSpan())));
        Assert.Equal(before,
            backing);

        byte[] payload = ScalarEncode(MakeValues(4));
        var original = payload.ToArray();
        Assert.Throws<ArgumentException>(() => Int64ArrayConverter.Read(payload.AsSpan(),
            MemoryMarshal.Cast<byte, long>(payload.AsSpan(0,
                32))));
        var sequence = TestWire.Chunks(payload.AsMemory(0,
                24),
            payload.AsMemory(24));
        Assert.Throws<ArgumentException>(() => Int64ArrayConverter.Read(sequence,
            MemoryMarshal.Cast<byte, long>(payload.AsSpan(24,
                32))));
        Assert.Equal(original,
            payload);
    }

    [Fact]
    public void CompleteBindAndDataRowFramesPreserveArrayAndOuterLengths()
    {
        long[] values = [0x0102030405060708];
        byte[] payload = Encode(values);
        var bind = FrontendMessage.Bind(parameters: new ReadOnlyMemory<byte>?[]
            {
                payload
            },
            parameterFormats: new[]
            {
                FormatCode.Binary
            },
            resultFormats: new[]
            {
                FormatCode.Binary
            });
        byte[] packet = new byte[bind.GetByteCount()];
        bind.Write(packet);
        Assert.Equal(TestWire.Bytes("42 00000034 00 00 0001 0001 0001 00000020 " + "00000001 00000000 00000014 00000001 00000001 00000008 0102030405060708 0001 0001"),
            packet);

        byte[] row = TestWire.Bytes("44 0000002a 0001 00000020 " +
                                    "00000001 00000000 00000014 00000001 00000001 00000008 0102030405060708");
        var input = TestWire.ByteSegments(row);
        var storage = new ReadOnlySequence<byte>?[1];
        Assert.True(BackendMessageReader.TryRead(ref input,
            storage,
            out _,
            out var indexedRow));
        Assert.True(input.IsEmpty);
        Assert.Equal(values,
            Int64ArrayConverter.Read(indexedRow.Values.Span[0]!.Value)
                .ToArray());
    }

    private static void AssertInvalid(byte[] bytes)
    {
        Assert.Throws<InvalidDataException>(() => Int64ArrayConverter.Read(bytes.AsSpan()));
        Assert.Throws<InvalidDataException>(() => Int64ArrayConverter.Read(TestWire.ByteSegments(bytes)));
        var storage = Enumerable.Repeat(42L,
            9).ToArray();
        Assert.Throws<InvalidDataException>(() => Int64ArrayConverter.Read(bytes.AsSpan(),
            storage));
        Assert.Throws<InvalidDataException>(() => Int64ArrayConverter.Read(TestWire.ByteSegments(bytes),
            storage));
        Assert.All(storage,
            value => Assert.Equal(42,
                value));
    }

    private static byte[] Encode(ReadOnlyMemory<long> values)
    {
        byte[] bytes = new byte[Int64ArrayConverter.GetByteCount(values)];
        Int64ArrayConverter.Write(values,
            bytes);
        return bytes;
    }

    private static byte[] ScalarEncode(ReadOnlySpan<long> values)
    {
        byte[] bytes = new byte[values.IsEmpty ? 12 : 20 + 12 * values.Length];
        BinaryPrimitives.WriteInt32BigEndian(bytes,
            values.IsEmpty
                ? 0
                : 1);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8),
            20);
        if (values.IsEmpty)
        {
            return bytes;
        }
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(12),
            values.Length);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16),
            1);
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20 + i * 12),
                8);
            BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(24 + i * 12),
                values[i]);
        }
        return bytes;
    }

    private static long[] MakeValues(int count)
    {
        var random = new Random(42);
        byte[] bytes = new byte[count * 8];
        random.NextBytes(bytes);
        return [.. MemoryMarshal.Cast<byte, long>(bytes)];
    }

    private sealed class RecordingWriter(int length) : IBufferWriter<byte>
    {
        public byte[] Bytes { get; } =
        [
            .. Enumerable.Repeat((byte)0xcc,
                length + 37)
        ];
        public int SizeHint { get; private set; }
        public int Advanced { get; private set; }
        public int GetSpanCalls { get; private set; }
        public int AdvanceCalls { get; private set; }
        public void Advance(int count)
        {
            AdvanceCalls++;
            Advanced += count;
        }
        public Memory<byte> GetMemory(int sizeHint = 0) => throw new InvalidOperationException("Use GetSpan.");
        public Span<byte> GetSpan(int sizeHint = 0)
        {
            GetSpanCalls++;
            SizeHint = sizeHint;
            return Bytes;
        }
    }
}