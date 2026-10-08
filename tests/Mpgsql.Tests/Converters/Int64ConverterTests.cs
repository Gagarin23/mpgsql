using System.Buffers;
using Mpgsql.Converters;
using Mpgsql.Protocol;
using Mpgsql.Tests.Protocol;

namespace Mpgsql.Tests.Converters;

public sealed class Int64ConverterTests
{
    public static TheoryData<long, string> Payloads => new TheoryData<long, string>
    {
        {
            long.MinValue, "8000000000000000"
        },
        {
            long.MaxValue, "7fffffffffffffff"
        },
        {
            -1, "ffffffffffffffff"
        },
        {
            0, "0000000000000000"
        },
        {
            1, "0000000000000001"
        },
        {
            0x0102030405060708, "0102030405060708"
        },
        {
            -0x0102030405060708, "fefdfcfbfaf9f8f8"
        }
    };

    [Theory, MemberData(nameof(Payloads))]
    public void WritesLiteralPayloadWithoutChangingOtherBytes(
        long value,
        string hex
    )
    {
        var expected = TestWire.Bytes(hex);
        byte[] bytes =
        [
            .. Enumerable.Repeat
            (
                (byte)0xcc,
                16
            )
        ];
        Assert.Equal
        (
            8,
            Int64Converter.GetByteCount(value)
        );
        Assert.Equal
        (
            8,
            Int64Converter.GetByteCount((long?)value)
        );
        Assert.Equal
        (
            8,
            Int64Converter.Write
            (
                value,
                bytes.AsSpan(3)
            )
        );
        Assert.Equal
        (
            expected,
            bytes
                .AsSpan
                (
                    3,
                    8
                )
                .ToArray()
        );
        Assert.All
        (
            bytes[..3]
                .Concat(bytes[11..]),
            b => Assert.Equal
            (
                (byte)0xcc,
                b
            )
        );
        bytes
            .AsSpan()
            .Fill(0xcc);
        Assert.Equal
        (
            8,
            Int64Converter.Write
            (
                (long?)value,
                bytes.AsSpan(3)
            )
        );
        Assert.Equal
        (
            expected,
            bytes
                .AsSpan
                (
                    3,
                    8
                )
                .ToArray()
        );
        Assert.All
        (
            bytes[..3]
                .Concat(bytes[11..]),
            b => Assert.Equal
            (
                (byte)0xcc,
                b
            )
        );
        var writer = new RecordingWriter();
        Int64Converter.Write
        (
            value,
            writer
        );
        AssertWriter
        (
            writer,
            expected
        );
        writer = new RecordingWriter();
        Int64Converter.Write
        (
            (long?)value,
            writer
        );
        AssertWriter
        (
            writer,
            expected
        );
    }

    [Theory, MemberData(nameof(Payloads))]
    public void ReadsContiguousAndEverySegmentBoundary(
        long value,
        string hex
    )
    {
        var bytes = TestWire.Bytes(hex);
        Assert.Equal
        (
            value,
            Int64Converter.Read(bytes.AsSpan())
        );
        Assert.Equal
        (
            value,
            Int64Converter.Read(new ReadOnlySequence<byte>(bytes))
        );
        Assert.Equal
        (
            value,
            Int64Converter.ReadNullable((ReadOnlyMemory<byte>?)bytes)
        );
        Assert.Equal
        (
            value,
            Int64Converter.ReadNullable(new ReadOnlySequence<byte>(bytes))
        );
        for (var split = 0;
             split <= bytes.Length;
             split++)
        {
            var input = TestWire.Chunks
            (
                ReadOnlyMemory<byte>.Empty,
                bytes.AsMemory
                (
                    0,
                    split
                ),
                ReadOnlyMemory<byte>.Empty,
                bytes.AsMemory(split),
                ReadOnlyMemory<byte>.Empty
            );
            Assert.Equal
            (
                value,
                Int64Converter.Read(input)
            );
            Assert.Equal
            (
                value,
                Int64Converter.ReadNullable(input)
            );
        }
        Assert.Equal
        (
            value,
            Int64Converter.Read(TestWire.ByteSegments(bytes))
        );
    }

    [Fact]
    public void NullHasNoPayloadAndDoesNotTouchOrReserveDestination()
    {
        byte[] bytes =
        [
            .. Enumerable.Repeat
            (
                (byte)0xcc,
                16
            )
        ];
        Assert.Equal
        (
            0,
            Int64Converter.GetByteCount(null)
        );
        Assert.Equal
        (
            0,
            Int64Converter.Write
            (
                null,
                bytes
            )
        );
        Assert.Equal
        (
            0,
            Int64Converter.Write
            (
                null,
                Span<byte>.Empty
            )
        );
        Assert.All
        (
            bytes,
            b => Assert.Equal
            (
                (byte)0xcc,
                b
            )
        );
        var writer = new RecordingWriter();
        Int64Converter.Write
        (
            null,
            writer
        );
        Assert.Equal
        (
            0,
            writer.Reservations
        );
        Assert.Equal
        (
            0,
            writer.Advances
        );
        Assert.All
        (
            writer.Bytes,
            b => Assert.Equal
            (
                (byte)0xcc,
                b
            )
        );
        Assert.Null(Int64Converter.ReadNullable((ReadOnlyMemory<byte>?)null));
        Assert.Null(Int64Converter.ReadNullable((ReadOnlySequence<byte>?)null));
        Assert.Throws<ArgumentNullException>
        (() => Int64Converter.Write
            (
                1L,
                (IBufferWriter<byte>)null!
            )
        );
        Assert.Throws<ArgumentNullException>
        (() => Int64Converter.Write
            (
                null,
                (IBufferWriter<byte>)null!
            )
        );
    }

    [Theory, InlineData(0), InlineData(1), InlineData(2), InlineData(3), InlineData(4), InlineData(5), InlineData(6), InlineData(7)]
    public void InsufficientCapacityPrecedesMutationAndAdvance(int capacity)
    {
        byte[] bytes =
        [
            .. Enumerable.Repeat
            (
                (byte)0xcc,
                capacity
            )
        ];
        Assert.Throws<ArgumentException>
        (() => Int64Converter.Write
            (
                42L,
                bytes
            )
        );
        Assert.Throws<ArgumentException>
        (() => Int64Converter.Write
            (
                (long?)42,
                bytes
            )
        );
        Assert.All
        (
            bytes,
            b => Assert.Equal
            (
                (byte)0xcc,
                b
            )
        );
        // A writer violating GetSpan's contract must not be advanced or partially written.
        var writer = new RecordingWriter(capacity);
        Assert.Throws<ArgumentException>
        (() => Int64Converter.Write
            (
                42L,
                writer
            )
        );
        Assert.Equal
        (
            0,
            writer.Advances
        );
        Assert.All
        (
            writer.Bytes,
            b => Assert.Equal
            (
                (byte)0xcc,
                b
            )
        );
    }

    [Theory, InlineData(0), InlineData(1), InlineData(2), InlineData(3), InlineData(4), InlineData(5), InlineData(6), InlineData(7), InlineData(9), InlineData(16)]
    public void EmptyTruncatedAndTrailingPayloadsAreInvalidIncludingNullableFields(int length)
    {
        var bytes = new byte[length];
        Assert.Throws<InvalidDataException>(() => Int64Converter.Read(bytes.AsSpan()));
        Assert.Throws<InvalidDataException>(() => Int64Converter.Read(new ReadOnlySequence<byte>(bytes)));
        Assert.Throws<InvalidDataException>(() => Int64Converter.Read(TestWire.ByteSegments(bytes)));
        Assert.Throws<InvalidDataException>(() => Int64Converter.ReadNullable((ReadOnlyMemory<byte>?)bytes));
        Assert.Throws<InvalidDataException>(() => Int64Converter.ReadNullable(TestWire.ByteSegments(bytes)));
    }

    [Fact]
    public void PayloadMemorySliceAndReadResultDoNotDependOnBorrowedStorage()
    {
        var bytes = TestWire.Bytes("cccc 0102030405060708 dddd");
        var payload = bytes.AsMemory
        (
            2,
            8
        );
        var result = Int64Converter.ReadNullable(payload);
        Assert.Equal
        (
            0x0102030405060708L,
            result
        );
        bytes
            .AsSpan()
            .Clear();
        Assert.Equal
        (
            0x0102030405060708L,
            result
        );
    }

    [Fact]
    public void CompleteBindAndDataRowBytesDistinguishNumberAndSqlNull()
    {
        var payload = new byte[8];
        Int64Converter.Write
        (
            0x0102030405060708L,
            payload
        );
        var bind = FrontendMessage.Bind
        (
            parameters: new ReadOnlyMemory<byte>?[]
            {
                payload,
                null
            },
            parameterFormats: new[]
            {
                FormatCode.Binary
            },
            resultFormats: new[]
            {
                FormatCode.Binary
            }
        );
        var packet = new byte[bind.GetByteCount()];
        bind.Write(packet);
        Assert.Equal
        (
            TestWire.Bytes("42 00000020 00 00 0001 0001 0002 00000008 0102030405060708 ffffffff 0001 0001"),
            packet
        );
        var input = TestWire.ByteSegments(TestWire.Bytes("44 00000016 0002 00000008 0102030405060708 ffffffff"));
        var fields = new ReadOnlySequence<byte>?[2];
        Assert.True
        (
            BackendMessageReader.TryRead
            (
                ref input,
                fields,
                out _,
                out var row
            )
        );
        Assert.True(input.IsEmpty);
        Assert.Equal
        (
            0x0102030405060708L,
            Int64Converter.ReadNullable(row.Values.Span[0])
        );
        Assert.Null(Int64Converter.ReadNullable(row.Values.Span[1]));
    }

    private static void AssertWriter(
        RecordingWriter writer,
        byte[] expected
    )
    {
        Assert.Equal
        (
            8,
            writer.SizeHint
        );
        Assert.Equal
        (
            8,
            writer.Advanced
        );
        Assert.Equal
        (
            1,
            writer.Reservations
        );
        Assert.Equal
        (
            1,
            writer.Advances
        );
        Assert.Equal
        (
            expected,
            writer.Bytes[..8]
        );
        Assert.All
        (
            writer.Bytes[8..],
            b => Assert.Equal
            (
                (byte)0xcc,
                b
            )
        );
    }

    private sealed class RecordingWriter(int capacity = 16) : IBufferWriter<byte>
    {
        internal int SizeHint,
            Advanced,
            Reservations,
            Advances;

        internal byte[] Bytes { get; } =
        [
            .. Enumerable.Repeat
            (
                (byte)0xcc,
                capacity
            )
        ];
        public Span<byte> GetSpan(int sizeHint = 0)
        {
            SizeHint = sizeHint;
            Reservations++;
            return Bytes;
        }
        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            throw new NotSupportedException();
        }
        public void Advance(int count)
        {
            Advanced = count;
            Advances++;
        }
    }
}