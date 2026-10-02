using System.Buffers;
using System.Buffers.Binary;
using Mpgsql.Copy;
using Mpgsql.Protocol;
using Mpgsql.Tests.Protocol;

namespace Mpgsql.Tests.Copy;

public class BinaryCopyTests
{
    private const string Header = "5047434f50590aff0d0a00 00000000 00000000";

    [Fact]
    public void NullableInt64UsesOuterNullLengthAndPreservesRowState()
    {
        var output = new ArrayBufferWriter<byte>();
        var writer = new BinaryCopyWriter(output,
            4);
        Assert.Throws<InvalidOperationException>(() => writer.WriteInt64((long?)null));
        writer.StartRow();
        writer.WriteInt64((long?)long.MinValue);
        writer.WriteInt64((long?)null);
        writer.WriteInt64((long?)0);
        writer.WriteInt64((long?)long.MaxValue);
        Assert.Equal(1ul,
            writer.Complete());
        Assert.Throws<InvalidOperationException>(() => writer.WriteInt64((long?)null));
        Assert.Equal(TestWire.Bytes(Header + " 0004 00000008 8000000000000000 ffffffff" + " 00000008 0000000000000000 00000008 7fffffffffffffff ffff"),
            output.WrittenSpan.ToArray());
        var input = TestWire.ByteSegments([.. output.WrittenSpan]);
        var reader = new BinaryCopyReader(4);
        Assert.True(reader.TryReadHeader(ref input));
        Assert.Equal(BinaryCopyReadStatus.Row,
            reader.TryReadRow(ref input,
                new ReadOnlySequence<byte>?[4],
                out var row));
        Assert.Equal(long.MinValue,
            row.ReadNullableInt64(0));
        Assert.Null(row.ReadNullableInt64(1));
        Assert.Equal(0L,
            row.ReadNullableInt64(2));
        Assert.Equal(long.MaxValue,
            row.ReadNullableInt64(3));
        Assert.Throws<InvalidOperationException>(() => row.ReadInt64(1));
        Assert.Equal(BinaryCopyReadStatus.Completed,
            reader.TryReadRow(ref input,
                new ReadOnlySequence<byte>?[4],
                out _));
    }

    [Fact]
    public void WritesCompleteLiteralStreamAndFramedPacket()
    {
        using var stream = new MemoryStream();
        using var output = new CopyDataWriter(stream,
            128);
        var writer = new BinaryCopyWriter(output,
            3);
        writer.StartRow();
        writer.WriteInt64(0x0102030405060708);
        writer.WriteNull();
        writer.WriteRaw([]);
        Assert.Equal(1ul,
            writer.Complete());
        output.Flush();
        byte[] payload = TestWire.Bytes(Header + " 0003 00000008 0102030405060708 ffffffff 00000000 ffff");
        byte[] expected = new byte[5 + payload.Length];
        expected[0] = (byte)'d';
        BinaryPrimitives.WriteInt32BigEndian(expected.AsSpan(1),
            payload.Length + 4);
        payload.CopyTo(expected,
            5);
        Assert.Equal(expected,
            stream.ToArray());
        var reader = new BinaryCopyReader(3);
        var input = TestWire.ByteSegments(payload);
        Assert.True(reader.TryReadHeader(ref input));
        var fields = new ReadOnlySequence<byte>?[3];
        Assert.Equal(BinaryCopyReadStatus.Row,
            reader.TryReadRow(ref input,
                fields,
                out var row));
        Assert.Equal(0x0102030405060708,
            row.ReadInt64(0));
        Assert.True(row.IsNull(1));
        Assert.Throws<InvalidOperationException>(() => row.ReadInt64(1));
        Assert.False(row.IsNull(2));
        Assert.True(row[2]!.Value.IsEmpty);
        Assert.Equal(BinaryCopyReadStatus.Completed,
            reader.TryReadRow(ref input,
                fields,
                out _));
        Assert.True(input.IsEmpty);
        reader.EndData();
    }

    [Fact]
    public void WritesLiteralBigintArrayFieldWithoutTemporaryPayload()
    {
        var output = new ArrayBufferWriter<byte>();
        var writer = new BinaryCopyWriter(output,
            1);
        writer.StartRow();
        writer.WriteLongArray(new long[] {-1, long.MinValue});
        writer.Complete();
        Assert.Equal(TestWire.Bytes(Header + " 0001 0000002c 00000001 00000000 00000014 00000002 00000001" + " 00000008 ffffffffffffffff 00000008 8000000000000000 ffff"),
            output.WrittenSpan.ToArray());
        var input = TestWire.ByteSegments([.. output.WrittenSpan]);
        var reader = new BinaryCopyReader(1);
        reader.TryReadHeader(ref input);
        var fields = new ReadOnlySequence<byte>?[1];
        Assert.Equal(BinaryCopyReadStatus.Row,
            reader.TryReadRow(ref input,
                fields,
                out var row));
        Assert.Equal(new long[]
            {
                -1,
                long.MinValue
            },
            row.ReadLongArray(0)
                .ToArray());
        var destination = new long[2];
        Assert.Equal(2,
            row.ReadLongArray(0,
                destination));
        Assert.Equal(new long[]
            {
                -1,
                long.MinValue
            },
            destination);
    }

    [Fact]
    public void WritesLiteralNullableBigintArrayAndDistinguishesOuterNull()
    {
        var output = new ArrayBufferWriter<byte>();
        var writer = new BinaryCopyWriter(output,
            3);
        writer.StartRow();
        writer.WriteNullableLongArray(new long?[] {null, long.MinValue, null});
        writer.WriteNullableLongArray(default);
        writer.WriteNull();
        writer.Complete();
        Assert.Equal(TestWire.Bytes(Header + " 0003 00000028 00000001 00000001 00000014 00000003 00000001" + " ffffffff 00000008 8000000000000000 ffffffff" + " 0000000c 00000000 00000000 00000014 ffffffff ffff"),
            output.WrittenSpan.ToArray());
        var input = TestWire.ByteSegments([.. output.WrittenSpan]);
        var reader = new BinaryCopyReader(3);
        Assert.True(reader.TryReadHeader(ref input));
        Assert.Equal(BinaryCopyReadStatus.Row,
            reader.TryReadRow(ref input,
                new ReadOnlySequence<byte>?[3],
                out var row));
        Assert.Equal([
                null,
                long.MinValue,
                null
            ],
            row.ReadNullableLongArray(0)
                .ToArray());
        long?[] reused = [42, 42, 42];
        Assert.Equal(3,
            row.ReadNullableLongArray(0,
                reused));
        Assert.Equal([
                null,
                long.MinValue,
                null
            ],
            reused);
        Assert.True(row.ReadNullableLongArray(1).IsEmpty);
        Assert.True(row.IsNull(2));
        Assert.Throws<InvalidOperationException>(() => row.ReadNullableLongArray(2));
        Assert.Equal(BinaryCopyReadStatus.Completed,
            reader.TryReadRow(ref input,
                new ReadOnlySequence<byte>?[3],
                out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(4096)]
    public void BigintBatchMatchesScalarRows(int count)
    {
        long[] values =
        [
            .. Enumerable.Range(0,
                count).Select(i => unchecked(long.MinValue + i))
        ];
        var scalar = new ArrayBufferWriter<byte>();
        var scalarWriter = new BinaryCopyWriter(scalar,
            1);
        foreach (long value in values)
        {
            scalarWriter.StartRow();
            scalarWriter.WriteInt64(value);
        }
        scalarWriter.Complete();
        var batch = new ArrayBufferWriter<byte>();
        var batchWriter = new BinaryCopyWriter(batch,
            1);
        batchWriter.WriteInt64Rows(values);
        Assert.Equal((ulong)count,
            batchWriter.Complete());
        Assert.Equal(scalar.WrittenSpan.ToArray(),
            batch.WrittenSpan.ToArray());
    }

    [Fact]
    public void HeaderExtensionsAndNonCriticalFlagsAreSkippedAcrossSegments()
    {
        var input = TestWire.ByteSegments(TestWire.Bytes("5047434f50590aff0d0a00 00008001 00000003 aabbcc ffff"));
        var reader = new BinaryCopyReader(0);
        Assert.True(reader.TryReadHeader(ref input));
        Assert.Equal(BinaryCopyReadStatus.Completed,
            reader.TryReadRow(ref input,
                Memory<ReadOnlySequence<byte>?>.Empty,
                out _));
        Assert.True(input.IsEmpty);
    }

    [Fact]
    public void EveryTruncatedHeaderLeavesInputUnchanged()
    {
        byte[] header = TestWire.Bytes("5047434f50590aff0d0a00 00000001 00000003 aabbcc");
        for (int length = 0; length < header.Length; length++)
        {
            var reader = new BinaryCopyReader(1);
            var input = TestWire.ByteSegments(header[..length]);
            var original = input;
            Assert.False(reader.TryReadHeader(ref input));
            Assert.Equal(original.Length,
                input.Length);
            Assert.Equal(original.Start,
                input.Start);
            Assert.False(reader.HeaderRead);
        }
    }

    [Fact]
    public void EveryTruncatedRowLeavesInputAndFieldStorageUnchanged()
    {
        byte[] bytes = TestWire.Bytes("0002 00000008 0102030405060708 00000003 aabbcc");
        for (int length = 0; length < bytes.Length; length++)
        {
            var reader = ReadyReader(2);
            var input = TestWire.ByteSegments(bytes[..length]);
            var original = input;
            var sentinel = new ReadOnlySequence<byte>([42]);
            ReadOnlySequence<byte>?[] fields = [sentinel, sentinel];
            Assert.Equal(BinaryCopyReadStatus.NeedMoreData,
                reader.TryReadRow(ref input,
                    fields,
                    out _));
            Assert.Equal(original.Length,
                input.Length);
            Assert.Equal(original.Start,
                input.Start);
            Assert.Equal(new byte[]
                {
                    42
                },
                fields[0]!.Value.ToArray());
            Assert.Equal(new byte[]
                {
                    42
                },
                fields[1]!.Value.ToArray());
            Assert.Equal(0ul,
                reader.RowsRead);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SingleColumnPathIsAtomicAndMatchesSegmentedParsing(bool fragmented)
    {
        byte[] bytes = TestWire.Bytes("0001 00000008 8000000000000000");
        for (int length = 0; length <= bytes.Length; length++)
        {
            var reader = ReadyReader(1);
            var input = fragmented ? TestWire.ByteSegments(bytes[..length]) : new ReadOnlySequence<byte>(bytes.AsMemory(0,
                length));
            var original = input;
            ReadOnlySequence<byte>?[] fields = [new ReadOnlySequence<byte>([42])];
            var status = reader.TryReadRow(ref input,
                fields,
                out var row);
            if (length != bytes.Length)
            {
                Assert.Equal(BinaryCopyReadStatus.NeedMoreData,
                    status);
                Assert.Equal(original.Length,
                    input.Length);
                Assert.Equal(original.Start,
                    input.Start);
                Assert.Equal(new byte[]
                    {
                        42
                    },
                    fields[0]!.Value.ToArray());
            }
            else
            {
                Assert.Equal(BinaryCopyReadStatus.Row,
                    status);
                Assert.Equal(long.MinValue,
                    row.ReadInt64(0));
                Assert.True(input.IsEmpty);
            }
        }
        var parser = ReadyReader(1);
        var tuples = new ReadOnlySequence<byte>(TestWire.Bytes("0001 ffffffff 0001 00000000 ffff"));
        var storage = new ReadOnlySequence<byte>?[1];
        Assert.Equal(BinaryCopyReadStatus.Row,
            parser.TryReadRow(ref tuples,
                storage,
                out var nullRow));
        Assert.True(nullRow.IsNull(0));
        Assert.Equal(BinaryCopyReadStatus.Row,
            parser.TryReadRow(ref tuples,
                storage,
                out var emptyRow));
        Assert.False(emptyRow.IsNull(0));
        Assert.True(emptyRow[0]!.Value.IsEmpty);
        Assert.Equal(BinaryCopyReadStatus.Completed,
            parser.TryReadRow(ref tuples,
                storage,
                out _));
    }

    [Theory]
    [InlineData("5047434f50590aff0d0a01 00000000 00000000")]
    [InlineData("5047434f50590aff0d0a00 00000000 ffffffff")]
    [InlineData("5047434f50590aff0d0a00 00000000 00100001")]
    public void RejectsInvalidHeader(string hex)
    {
        var input = TestWire.ByteSegments(TestWire.Bytes(hex));
        Assert.Throws<InvalidDataException>(() => new BinaryCopyReader(1).TryReadHeader(ref input));
    }

    [Theory]
    [InlineData("00010000")]
    [InlineData("80000000")]
    [InlineData("40000000")]
    public void RejectsCriticalFlagsIncludingOids(string flags)
    {
        var input = TestWire.ByteSegments(TestWire.Bytes("5047434f50590aff0d0a00 " + flags + " 00000000"));
        Assert.Throws<NotSupportedException>(() => new BinaryCopyReader(1).TryReadHeader(ref input));
    }

    [Theory]
    [InlineData("fffe")]
    [InlineData("0002")]
    [InlineData("0001 fffffffe")]
    [InlineData("0001 04000001")]
    [InlineData("ffff 00")]
    public void RejectsInvalidTupleWithoutConsumingInput(string hex)
    {
        foreach (bool fragmented in new[] {false, true})
        {
            var reader = ReadyReader(1);
            byte[] bytes = TestWire.Bytes(hex);
            var input = fragmented ? TestWire.ByteSegments(bytes) : new ReadOnlySequence<byte>(bytes);
            var original = input;
            Assert.Throws<InvalidDataException>(() => reader.TryReadRow(ref input,
                new ReadOnlySequence<byte>?[1],
                out _));
            Assert.Equal(original.Length,
                input.Length);
        }
    }

    [Fact]
    public void RejectsTruncatedEndAndBytesAfterTrailer()
    {
        Assert.Throws<InvalidDataException>(() => new BinaryCopyReader(1).EndData());
        var reader = ReadyReader(1);
        Assert.Throws<InvalidDataException>(() => reader.EndData());
        var input = new ReadOnlySequence<byte>(TestWire.Bytes("ffff"));
        Assert.Equal(BinaryCopyReadStatus.Completed,
            reader.TryReadRow(ref input,
                Memory<ReadOnlySequence<byte>?>.Empty,
                out _));
        input = new ReadOnlySequence<byte>([42]);
        Assert.Throws<InvalidDataException>(() => reader.TryReadRow(ref input,
            Memory<ReadOnlySequence<byte>?>.Empty,
            out _));
    }

    [Fact]
    public void EnforcesRowAndWriterLifecycleBeforeChangingOutput()
    {
        var output = new ArrayBufferWriter<byte>();
        Assert.Throws<ArgumentOutOfRangeException>(() => new BinaryCopyWriter(output,
            -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BinaryCopyWriter(output,
            32768));
        Assert.Equal(0,
            output.WrittenCount);
        var writer = new BinaryCopyWriter(output,
            2);
        Assert.Throws<InvalidOperationException>(() => writer.WriteNull());
        writer.StartRow();
        writer.WriteInt64(1);
        int written = output.WrittenCount;
        Assert.Throws<InvalidOperationException>(() => writer.StartRow());
        Assert.Throws<InvalidOperationException>(() => writer.Complete());
        Assert.Throws<InvalidOperationException>(() => writer.WriteInt64Rows([1]));
        Assert.Equal(written,
            output.WrittenCount);
        writer.WriteNull();
        Assert.Throws<InvalidOperationException>(() => writer.WriteNull());
        writer.Complete();
        Assert.Throws<InvalidOperationException>(() => writer.StartRow());
        Assert.Throws<InvalidOperationException>(() => writer.Complete());
    }

    [Fact]
    public void SupportsZeroColumnRowsAndChecksReusableFieldCapacity()
    {
        var output = new ArrayBufferWriter<byte>();
        var writer = new BinaryCopyWriter(output,
            0);
        writer.StartRow();
        writer.StartRow();
        Assert.Equal(2ul,
            writer.Complete());
        Assert.Equal(TestWire.Bytes(Header + " 0000 0000 ffff"),
            output.WrittenSpan.ToArray());
        var reader = ReadyReader(1);
        var input = new ReadOnlySequence<byte>(TestWire.Bytes("0001 ffffffff"));
        Assert.Throws<ArgumentException>(() => reader.TryReadRow(ref input,
            Memory<ReadOnlySequence<byte>?>.Empty,
            out _));
    }

    [Theory]
    [InlineData(19)]
    [InlineData(31)]
    [InlineData(8192)]
    public void FramesOnlyFilledPooledBytesAndPreservesTheBinaryStream(int bufferSize)
    {
        using var stream = new MemoryStream();
        using var output = new CopyDataWriter(stream,
            bufferSize);
        var writer = new BinaryCopyWriter(output,
            1);
        writer.StartRow();
        writer.WriteRaw(new byte[20000]); // Forces a larger reservation.
        writer.StartRow();
        writer.WriteNull();
        writer.Complete();
        output.Flush();
        var packets = new ReadOnlySequence<byte>(stream.ToArray());
        var payload = new ArrayBufferWriter<byte>();
        while (!packets.IsEmpty)
        {
            Assert.True(BackendMessageReader.TryRead(ref packets,
                out var message));
            Assert.Equal(BackendMessageKind.CopyData,
                message.Kind);
            foreach (var segment in message.GetCopyData()) payload.Write(segment.Span);
        }
        var reader = new BinaryCopyReader(1);
        var input = new ReadOnlySequence<byte>(payload.WrittenMemory);
        Assert.True(reader.TryReadHeader(ref input));
        var fields = new ReadOnlySequence<byte>?[1];
        Assert.Equal(BinaryCopyReadStatus.Row,
            reader.TryReadRow(ref input,
                fields,
                out var row));
        Assert.Equal(20000,
            row[0]!.Value.Length);
        Assert.Equal(BinaryCopyReadStatus.Row,
            reader.TryReadRow(ref input,
                fields,
                out row));
        Assert.True(row.IsNull(0));
        Assert.Equal(BinaryCopyReadStatus.Completed,
            reader.TryReadRow(ref input,
                fields,
                out _));
    }

    [Fact]
    public void DisposingBufferDoesNotSendOrCloseTheConnection()
    {
        using var stream = new MemoryStream();
        var output = new CopyDataWriter(stream);
        new BinaryCopyWriter(output,
            1);
        output.Dispose();
        output.Dispose();
        Assert.Equal(0,
            stream.Length);
        Assert.True(stream.CanWrite);
        Assert.Throws<ObjectDisposedException>(() => output.Flush());
    }

    [Fact]
    public void FailedPartialStreamWriteCannotBeRetried()
    {
        using var stream = new PartialFailureStream();
        using var output = new CopyDataWriter(stream);
        new BinaryCopyWriter(output,
            0).Complete();
        Assert.Throws<IOException>(() => output.Flush());
        Assert.Equal(2,
            stream.Length);
        Assert.Throws<InvalidOperationException>(() => output.Flush());
        Assert.Throws<InvalidOperationException>(() => output.GetMemory());
        Assert.Throws<InvalidOperationException>(() => output.Advance(0));
        Assert.Equal(2,
            stream.Length);
    }

    private sealed class PartialFailureStream : MemoryStream
    {
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            base.Write(buffer[..2]);
            throw new IOException("Intentional partial transport failure.");
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(512)]
    [InlineData(4096)]
    public void FinalFrameAndCopyDoneAreSentInOneWrite(int count)
    {
        using var stream = new CountingStream();
        using var output = new CopyDataWriter(stream,
            8192);
        var writer = new BinaryCopyWriter(output,
            1);
        for (int i = 0; i < count; i++)
        {
            writer.StartRow();
            writer.WriteInt64(i);
        }
        writer.Complete();
        int writes = stream.Writes;
        output.WriteCopyDone();
        Assert.Equal(writes + 1,
            stream.Writes);
        var input = new ReadOnlySequence<byte>(stream.ToArray());
        var payload = new ArrayBufferWriter<byte>();
        while (BackendMessageReader.TryRead(ref input,
                   out var message))
        {
            if (message.Kind == BackendMessageKind.CopyDone)
            {
                Assert.True(input.IsEmpty);
                break;
            }
            Assert.Equal(BackendMessageKind.CopyData,
                message.Kind);
            foreach (var segment in message.GetCopyData()) payload.Write(segment.Span);
        }
        Assert.Equal(TestWire.Bytes("63 00000004"),
            stream.ToArray()[^5..]);
        Assert.Equal(21 + 14 * count,
            payload.WrittenCount);
        Assert.Equal(TestWire.Bytes(Header),
            payload.WrittenSpan[..19]
                .ToArray());
        Assert.Equal(TestWire.Bytes("ffff"),
            payload.WrittenSpan[^2..]
                .ToArray());
        Assert.Throws<InvalidOperationException>(() => output.WriteCopyDone());
        Assert.Throws<InvalidOperationException>(() => output.Flush());
        Assert.Throws<InvalidOperationException>(() => output.GetMemory());
    }

    [Fact]
    public void CopyDoneWithNoBufferedDataDoesNotSendAnEmptyDataFrame()
    {
        using var stream = new CountingStream();
        using var output = new CopyDataWriter(stream);
        output.WriteCopyDone();
        Assert.Equal(1,
            stream.Writes);
        Assert.Equal(TestWire.Bytes("63 00000004"),
            stream.ToArray());
    }

    private sealed class CountingStream : MemoryStream
    {
        public int Writes { get; private set; }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Writes++;
            base.Write(buffer);
        }
    }

    private static BinaryCopyReader ReadyReader(int columns)
    {
        var reader = new BinaryCopyReader(columns);
        var header = new ReadOnlySequence<byte>(TestWire.Bytes(Header));
        Assert.True(reader.TryReadHeader(ref header));
        return reader;
    }
}