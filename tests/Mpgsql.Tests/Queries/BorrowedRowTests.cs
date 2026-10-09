using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Mpgsql.Internal;
using Mpgsql.Protocol;
using Mpgsql.Tests.Protocol;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class BorrowedRowTests
{
    [Fact]
    public void CompleteLiteralPayloadsPreserveNullEmptyAndArbitraryBytesAcrossEveryBoundary()
    {
        (string Hex, byte[]?[] Values)[] cases =
        [
            ("0000", []),
            ("0001ffffffff", [null]),
            ("000100000000", [[]]),
            ("0001000000040080ff01", [[0x00, 0x80, 0xff, 0x01]]),
            ("0003ffffffff0000000000000003010203", [null, [], [1, 2, 3]])
        ];
        var row = new BorrowedRow();
        foreach (var (hex, values) in cases)
        foreach (var input in Inputs(TestWire.Bytes(hex)))
        {
            row.Initialize(Message(input, values.Length));
            Assert.True(row.IsInitialized);
            Assert.Equal(values.Length, row.Count);
            for (var i = 0; i < values.Length; i++)
            {
                if (values[i] is { } expected)
                {
                    var value = row.GetValue(i);
                    Assert.True(value.HasValue);
                    Assert.Equal(expected, value!.Value.ToArray());
                }
                else
                {
                    Assert.Null(row.GetValue(i));
                }
            }
            Assert.Throws<ArgumentOutOfRangeException>(() => row.GetValue(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => row.GetValue(values.Length));
        }
    }

    [Fact]
    public void ReturnedFieldsBorrowTheExactInputBuffersWithoutFlattening()
    {
        foreach (var fragmented in new[] { false, true })
        {
            var bytes = TestWire.Bytes("0001000000080102030405060708");
            var input = fragmented ? TestWire.ByteSegments(bytes) : new ReadOnlySequence<byte>(bytes);
            var row = new BorrowedRow();
            row.Initialize(Message(input, 1));
            var value = row.GetValue(0)!.Value;
            if (fragmented)
            {
                Assert.False(value.IsSingleSegment);
            }
            foreach (var segment in value)
            {
                Assert.True(MemoryMarshal.TryGetArray(segment, out var storage));
                Assert.Same(bytes, storage.Array);
            }
            // Mutation proves that neither Initialize nor GetValue copied a field.
            // The test still owns the source; production keeps its ReadResult/frame owner alive.
            bytes[6] = 0x55;
            Assert.Equal((byte)0x55, value.ToArray()[0]);
            Assert.Equal((byte)0x55, row.GetValue(0)!.Value.ToArray()[0]);
        }
    }

    [Fact]
    public void RepeatedRowsWithDifferentColumnCountsCannotExposeStaleFields()
    {
        var row = new BorrowedRow();
        foreach (var count in new[] { 9, 1, 8, 17, 0, 9, 2, 64, 1 })
        {
            byte[]?[] values = Enumerable.Range(0, count).Select(i => (i % 3) switch
            {
                0 => (byte[]?)null,
                1 => Array.Empty<byte>(),
                _ => Int64(i + count)
            }).ToArray();
            var bytes = Row(values).AsSpan(5).ToArray();
            foreach (var input in new[] { new ReadOnlySequence<byte>(bytes), TestWire.ByteSegments(bytes) })
            {
                row.Initialize(Message(input, count));
                Assert.Equal(count, row.Count);
                for (var i = count - 1; i >= 0; i--)
                {
                    if (values[i] is { } value)
                    {
                        Assert.Equal(value, row.GetValue(i)!.Value.ToArray());
                    }
                    else
                    {
                        Assert.Null(row.GetValue(i));
                    }
                }
                Assert.Throws<ArgumentOutOfRangeException>(() => row.GetValue(count));
            }
        }
    }

    [Theory, InlineData(32768), InlineData(65535)]
    public void ColumnCountIsUnsignedAndSupportsTheCompleteWireRange(int count)
    {
        var bytes = new byte[2 + 4 * count];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)count);
        bytes.AsSpan(2).Fill(0xff);
        var row = new BorrowedRow();
        var inputs = new[]
        {
            new ReadOnlySequence<byte>(bytes),
            TestWire.Chunks(bytes.AsMemory(0, 1), ReadOnlyMemory<byte>.Empty, bytes.AsMemory(1, 4096), bytes.AsMemory(4097))
        };
        foreach (var input in inputs)
        {
            row.Initialize(Message(input, count));
            Assert.Equal(count, row.Count);
            Assert.Null(row.GetValue(0));
            Assert.Null(row.GetValue(count / 2));
            Assert.Null(row.GetValue(count - 1));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("00")]
    [InlineData("0001000000")]
    [InlineData("00020000000101000000")]
    [InlineData("00020000000400000000")]
    [InlineData("000100000001")]
    [InlineData("0001fffffffe")]
    [InlineData("000180000000")]
    [InlineData("00017fffffff")]
    [InlineData("ffff")]
    [InlineData("000000")]
    [InlineData("0001ffffffff00")]
    [InlineData("00010000000000")]
    public void MalformedLengthAndTrailingBytesRejectTheWholeRowAndDropItsPreviousPosition(string hex)
    {
        var bytes = TestWire.Bytes(hex);
        var count = bytes.Length < 2 ? 1 : BinaryPrimitives.ReadUInt16BigEndian(bytes);
        var valid = Message(new ReadOnlySequence<byte>(TestWire.Bytes("00010000000155")), 1);
        var row = new BorrowedRow();
        foreach (var input in Inputs(bytes))
        {
            row.Initialize(valid);
            Assert.Throws<InvalidDataException>(() => row.Initialize(Message(input, count)));
            Assert.False(row.IsInitialized);
            Assert.Equal(0, row.Count);
            Assert.Throws<InvalidOperationException>(() => row.GetValue(0));
            row.Initialize(valid);
            Assert.Equal(new byte[] { 0x55 }, row.GetValue(0)!.Value.ToArray());
        }
    }

    [Fact]
    public void CountPrefixMismatchAndOtherMessageKindsCannotPositionTheRow()
    {
        var row = new BorrowedRow();
        var bytes = TestWire.Bytes("00010000000155");
        foreach (var input in Inputs(bytes))
        {
            Assert.Throws<InvalidDataException>(() => row.Initialize(Message(input, 0)));
            Assert.False(row.IsInitialized);
        }
        Assert.Throws<InvalidOperationException>(() => row.Initialize(new BackendMessage(
            (byte)'C', BackendMessageKind.CommandComplete, new ReadOnlySequence<byte>(new byte[] { 0 }), 0)));
        Assert.False(row.IsInitialized);
    }

    [Fact]
    public void ResetRejectsFurtherAccessAndTheNextInitializationUsesOnlyItsNewInput()
    {
        var row = new BorrowedRow();
        Assert.False(row.IsInitialized);
        Assert.Throws<InvalidOperationException>(() => row.GetValue(0));
        row.Initialize(Message(new ReadOnlySequence<byte>(TestWire.Bytes("00010000000101")), 1));
        Assert.Equal(new byte[] { 1 }, row.GetValue(0)!.Value.ToArray());
        row.Reset();
        row.Reset();
        Assert.False(row.IsInitialized);
        Assert.Equal(0, row.Count);
        Assert.Throws<InvalidOperationException>(() => row.GetValue(0));
        row.Initialize(Message(TestWire.ByteSegments(TestWire.Bytes("0001000000020203")), 1));
        Assert.Equal(new byte[] { 2, 3 }, row.GetValue(0)!.Value.ToArray());
    }

    private static BackendMessage Message(ReadOnlySequence<byte> payload, int count)
        => new BackendMessage((byte)'D', BackendMessageKind.DataRow, payload, count);

    private static IEnumerable<ReadOnlySequence<byte>> Inputs(byte[] bytes)
    {
        yield return new ReadOnlySequence<byte>(bytes);
        yield return TestWire.ByteSegments(bytes);
        for (var split = 0; split <= bytes.Length; split++)
        {
            yield return TestWire.Chunks(
                ReadOnlyMemory<byte>.Empty, bytes.AsMemory(0, split),
                ReadOnlyMemory<byte>.Empty, bytes.AsMemory(split), ReadOnlyMemory<byte>.Empty);
        }
    }
}
