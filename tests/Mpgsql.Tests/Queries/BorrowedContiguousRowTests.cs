using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Mpgsql.Internal;
using Mpgsql.Protocol;
using Mpgsql.Tests.Protocol;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class BorrowedContiguousRowTests
{
    [Theory, InlineData(0), InlineData(1), InlineData(15), InlineData(37)]
    public void DirectMemoryAndMessageEntriesBorrowIdenticalBoundedFields(int prefix)
    {
        var direct = new BorrowedRow();
        var message = new BorrowedRow();
        foreach (var count in new[] { 9, 1, 8, 17, 0, 9, 2, 64, 1 })
        {
            byte[]?[] values = Enumerable.Range(0, count).Select(i => (i % 3) switch
            {
                0 => Int64(long.MinValue + i),
                1 => Array.Empty<byte>(),
                _ => (byte[]?)null
            }).ToArray();
            var payload = Row(values).AsSpan(5).ToArray();
            var storage = new byte[prefix + payload.Length + 19];
            storage.AsSpan().Fill(0xff);
            payload.CopyTo(storage.AsSpan(prefix));
            var memory = storage.AsMemory(prefix, payload.Length);
            direct.Initialize(memory, count);
            message.Initialize(Message(new ReadOnlySequence<byte>(memory), count));
            Assert.True(direct.IsInitialized);
            Assert.Equal(message.Count, direct.Count);
            for (var ordinal = count - 1; ordinal >= 0; ordinal--)
            {
                var actual = direct.GetValue(ordinal);
                var other = message.GetValue(ordinal);
                Assert.Equal(other.HasValue, actual.HasValue);
                if (values[ordinal] is { } expected)
                {
                    Assert.Equal(expected, actual!.Value.ToArray());
                    Assert.Equal(expected, other!.Value.ToArray());
                    Assert.True(actual.Value.IsSingleSegment);
                    Assert.True(MemoryMarshal.TryGetArray(actual.Value.First, out var borrowed));
                    Assert.Same(storage, borrowed.Array);
                }
            }
            Assert.Throws<ArgumentOutOfRangeException>(() => direct.GetValue(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => direct.GetValue(count));
            if (count != 0)
            {
                // Initialization and getters retain the caller's exact memory slice.
                storage[prefix + 6] = 0x55;
                Assert.Equal((byte)0x55, direct.GetValue(0)!.Value.FirstSpan[0]);
                Assert.Equal((byte)0x55, message.GetValue(0)!.Value.FirstSpan[0]);
            }
        }
    }

    [Fact]
    public void MalformedDirectMemoryAndMessageEntriesRejectIdenticalLengthAndCountBoundaries()
    {
        (string Hex, int Count)[] cases =
        [
            ("", 0), ("00", 0), ("0001000000", 1),
            ("00020000000101000000", 2), ("00020000000400000000", 2),
            ("000100000001", 1), ("0001fffffffe", 1), ("000180000000", 1),
            ("00017fffffff", 1), ("ffff", 65535), ("000000", 0),
            ("0001ffffffff00", 1), ("00010000000000", 1),
            ("00010000000155", 0), ("00010000000155", -1), ("00010000000155", 65536)
        ];
        var row = new BorrowedRow();
        var valid = TestWire.Bytes("00010000000155");
        foreach (var (hex, count) in cases)
        {
            var payload = TestWire.Bytes(hex);
            row.Initialize(valid.AsMemory(), 1);
            Assert.Throws<InvalidDataException>(() => row.Initialize(payload.AsMemory(), count));
            Assert.False(row.IsInitialized);
            Assert.Equal(0, row.Count);
            Assert.Throws<InvalidOperationException>(() => row.GetValue(0));
            row.Initialize(valid.AsMemory(), 1);
            Assert.Throws<InvalidDataException>(() => row.Initialize(Message(new ReadOnlySequence<byte>(payload), count)));
            Assert.False(row.IsInitialized);
            Assert.Equal(0, row.Count);
            Assert.Throws<InvalidOperationException>(() => row.GetValue(0));
        }
    }

    [Fact]
    public void DirectMemoryAndFragmentedRowsReplaceEachOthersBorrowedStorage()
    {
        var row = new BorrowedRow();
        var first = TestWire.Bytes("00010000000101");
        var second = TestWire.Bytes("0001000000020203");
        row.Initialize(first.AsMemory(), 1);
        Assert.Equal(new byte[] { 1 }, row.GetValue(0)!.Value.ToArray());
        row.Initialize(Message(TestWire.ByteSegments(second), 1));
        Assert.Equal(new byte[] { 2, 3 }, row.GetValue(0)!.Value.ToArray());
        row.Initialize(TestWire.Bytes("0001ffffffff").AsMemory(), 1);
        Assert.Null(row.GetValue(0));
        row.Initialize(Message(TestWire.ByteSegments(TestWire.Bytes("000100000000")), 1));
        Assert.True(row.GetValue(0)!.Value.IsEmpty);
        row.Reset();
        Assert.False(row.IsInitialized);
        Assert.Throws<InvalidOperationException>(() => row.GetValue(0));
        row.Initialize(first.AsMemory(), 1);
        Assert.Equal(new byte[] { 1 }, row.GetValue(0)!.Value.ToArray());
    }

    [Theory, InlineData(32768), InlineData(65535)]
    public void DirectMemoryCountRetainsTheCompleteUnsignedWireRange(int count)
    {
        var payload = new byte[2 + count * 4];
        BinaryPrimitives.WriteUInt16BigEndian(payload, (ushort)count);
        payload.AsSpan(2).Fill(0xff);
        var row = new BorrowedRow();
        row.Initialize(payload.AsMemory(), count);
        Assert.Equal(count, row.Count);
        Assert.Null(row.GetValue(0));
        Assert.Null(row.GetValue(count / 2));
        Assert.Null(row.GetValue(count - 1));
    }

    private static BackendMessage Message(ReadOnlySequence<byte> payload, int count)
        => new BackendMessage((byte)'D', BackendMessageKind.DataRow, payload, count);
}
