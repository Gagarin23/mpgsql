using System.Buffers;
using System.Buffers.Binary;
using Mpgsql.Protocol;

namespace Mpgsql.Tests.Protocol;

public sealed class BufferedDataRowTests
{
    private const string ThreeValues = "44 00000014 0003 ffffffff 00000000 00000002 c3a9";

    [Theory, MemberData(nameof(BackendMessageTests.Packets), MemberType = typeof(BackendMessageTests))]
    public void BufferedReaderPreservesEveryMessageAndSegmentBoundary(BackendMessageKind kind,
        string hex)
    {
        var bytes = TestWire.Bytes(hex);
        var storage = new ReadOnlySequence<byte>?[8];
        for (var split = 0; split <= bytes.Length; split++)
        {
            var input = TestWire.Chunks(bytes.AsMemory(0,
                    split),
                ReadOnlyMemory<byte>.Empty,
                bytes.AsMemory(split));
            Assert.True(BackendMessageReader.TryRead(ref input,
                storage,
                out var message,
                out var row));
            Assert.Equal(kind,
                message.Kind);
            Assert.Equal(bytes[5..],
                message.Payload.ToArray());
            Assert.True(input.IsEmpty);
            if (kind == BackendMessageKind.DataRow)
            {
                Assert.Equal(ReadValues(TestWire.Read(hex)
                        .GetDataRow()),
                    ReadValues(row));
            }
        }
    }

    [Theory, MemberData(nameof(BackendValidationTests.InvalidPackets), MemberType = typeof(BackendValidationTests))]
    public void BufferedReaderRetainsCompleteBodyValidation(string hex)
    {
        var input = TestWire.ByteSegments(TestWire.Bytes(hex));
        var before = input;
        var storage = new ReadOnlySequence<byte>?[8];
        Assert.Throws<InvalidDataException>(() => BackendMessageReader.TryRead(ref input,
            storage,
            out _,
            out _));
        Assert.Equal(before.Start,
            input.Start);
        Assert.Equal(before.End,
            input.End);
    }

    [Fact]
    public void IncompleteFramesDoNotTouchStorage()
    {
        var bytes = TestWire.Bytes(ThreeValues);
        for (var length = 0; length < bytes.Length; length++)
        {
            var storage = new ReadOnlySequence<byte>?[]
            {
                new ReadOnlySequence<byte>([42]), null, null
            };
            var input = TestWire.ByteSegments(bytes[..length]);
            var before = input;
            Assert.False(BackendMessageReader.TryRead(ref input,
                storage,
                out _,
                out _));
            Assert.Equal(before.Start,
                input.Start);
            Assert.Equal(new byte[]
                {
                    42
                },
                storage[0]!.Value.ToArray());
            Assert.Null(storage[1]);
            Assert.Null(storage[2]);
        }
    }

    [Fact]
    public void InsufficientStorageDoesNotConsumeInputOrOverwriteStorage()
    {
        var input = TestWire.ByteSegments(TestWire.Bytes(ThreeValues));
        var before = input;
        var storage = new ReadOnlySequence<byte>?[]
        {
            new ReadOnlySequence<byte>([42]), null
        };
        Assert.Throws<ArgumentException>(() => BackendMessageReader.TryRead(ref input,
            storage,
            out _,
            out _));
        Assert.Equal(before.Start,
            input.Start);
        Assert.Equal(before.End,
            input.End);
        Assert.Equal(new byte[]
            {
                42
            },
            storage[0]!.Value.ToArray());
        Assert.Null(storage[1]);
    }

    [Fact]
    public void StorageSlicesPreserveNullEmptyAndBorrowedSegmentedValues()
    {
        var bytes = TestWire.Bytes(ThreeValues);
        var input = TestWire.ByteSegments(bytes);
        var sentinel = new ReadOnlySequence<byte>([42]);
        var storage = new ReadOnlySequence<byte>?[] {sentinel, sentinel, sentinel, sentinel, sentinel};
        Assert.True(BackendMessageReader.TryRead(ref input,
            storage.AsMemory(1,
                3),
            out _,
            out var row));
        Assert.Equal(new byte[]
            {
                42
            },
            storage[0]!.Value.ToArray());
        Assert.Equal(new byte[]
            {
                42
            },
            storage[4]!.Value.ToArray());
        Assert.Null(storage[1]);
        Assert.True(storage[2]!.Value.IsEmpty);
        Assert.False(storage[3]!.Value.IsSingleSegment);
        Assert.Equal(new[]
            {
                null,
                "",
                "C3A9"
            },
            ReadValues(row));
        bytes[^1] = 42; // bytes are borrowed, never copied into the metadata buffer
        Assert.Equal(new[]
            {
                null,
                "",
                "C32A"
            },
            ReadValues(row));
    }

    [Fact]
    public void IndexedEnumerationDoesNotRereadValueLengthsOrColumnCount()
    {
        var bytes = TestWire.Bytes("44 0000000d 0001 00000003 010203");
        var input = TestWire.ByteSegments(bytes);
        var storage = new ReadOnlySequence<byte>?[1];
        Assert.True(BackendMessageReader.TryRead(ref input,
            storage,
            out var message,
            out var row));
        // Deliberately corrupt already-validated metadata to detect an accidental second parse.
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(5),
            ushort.MaxValue);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(7),
            int.MaxValue);
        Assert.Equal(1,
            message.GetDataRow()
                .Count);
        Assert.Equal(1,
            row.Count);
        Assert.Equal(new[]
            {
                "010203"
            },
            ReadValues(row));
        Assert.Equal(new[]
            {
                "010203"
            },
            ReadValues(row));
    }

    [Fact]
    public void ReusingStorageOnlyExposesTheNewRowColumnCount()
    {
        var storage = new ReadOnlySequence<byte>?[3];
        var input = TestWire.ByteSegments(TestWire.Bytes(ThreeValues));
        Assert.True(BackendMessageReader.TryRead(ref input,
            storage,
            out _,
            out _));
        input = TestWire.ByteSegments(TestWire.Bytes("44 0000000b 0001 00000001 2a"));
        Assert.True(BackendMessageReader.TryRead(ref input,
            storage,
            out _,
            out var row));
        Assert.Equal(1,
            row.Count);
        Assert.Equal(new[]
            {
                "2A"
            },
            ReadValues(row));
    }

    [Fact]
    public void SeparateStorageSupportsRetainingMoreThanOneRow()
    {
        var firstInput = TestWire.ByteSegments(TestWire.Bytes(ThreeValues));
        var secondInput = TestWire.ByteSegments(TestWire.Bytes("44 0000000b 0001 00000001 2a"));
        Assert.True(BackendMessageReader.TryRead(ref firstInput,
            new ReadOnlySequence<byte>?[3],
            out _,
            out var first));
        Assert.True(BackendMessageReader.TryRead(ref secondInput,
            new ReadOnlySequence<byte>?[1],
            out _,
            out var second));
        Assert.Equal(new[]
            {
                null,
                "",
                "C3A9"
            },
            ReadValues(first));
        Assert.Equal(new[]
            {
                "2A"
            },
            ReadValues(second));
    }

    [Fact]
    public void EmptyRowsAndNonRowsDoNotRequireStorage()
    {
        var input = TestWire.ByteSegments(TestWire.Bytes("44 00000006 0000"));
        Assert.True(BackendMessageReader.TryRead(ref input,
            Memory<ReadOnlySequence<byte>?>.Empty,
            out _,
            out var empty));
        Assert.Empty(ReadValues(empty));
        input = TestWire.ByteSegments(TestWire.Bytes("31 00000004"));
        Assert.True(BackendMessageReader.TryRead(ref input,
            Memory<ReadOnlySequence<byte>?>.Empty,
            out var complete,
            out var nonRow));
        Assert.Equal(BackendMessageKind.ParseComplete,
            complete.Kind);
        Assert.Equal(0,
            nonRow.Count);
    }

    private static string?[] ReadValues(DataRow row)
    {
        var values = new List<string?>();
        foreach (var value in row) values.Add(value.HasValue ? Convert.ToHexString(value.Value.ToArray()) : null);
        return [.. values];
    }

    private static string?[] ReadValues(IndexedDataRow row)
    {
        var values = new List<string?>();
        foreach (var value in row)
            values.Add(value.HasValue ? Convert.ToHexString(value.Value.ToArray()) : null);
        return [.. values];
    }
}