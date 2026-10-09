using System.Buffers;
using System.Buffers.Binary;
using Mpgsql.Internal;
using Mpgsql.Protocol;
using Mpgsql.Tests.Protocol;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class BorrowedRowArrayStorageTests
{
    [Theory, InlineData(2, 1), InlineData(8, 15), InlineData(9, 37)]
    public void MultipleFieldsBorrowTheExactArraySliceAndKeepNullEmptyAndBounds(int count, int prefix)
    {
        byte[]?[] values = Enumerable.Range(0, count).Select(i => i switch
        {
            0 => Int64(long.MinValue),
            1 => Int64(long.MaxValue),
            2 => (byte[]?)null,
            3 => Array.Empty<byte>(),
            _ => Int64(-i)
        }).ToArray();
        var payload = Row(values).AsSpan(5).ToArray();
        var storage = new byte[prefix + payload.Length + 19];
        storage.AsSpan().Fill(0x55);
        payload.CopyTo(storage.AsSpan(prefix));
        var row = new BorrowedRow();
        row.Initialize(storage.AsMemory(prefix, payload.Length), count);
        AssertFields(row, values);
        Assert.Throws<ArgumentOutOfRangeException>(() => row.TryGetContiguousValue(-1, out _, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => row.TryGetContiguousValue(count, out _, out _));

        Assert.True(row.TryGetContiguousValue(0, out var borrowed, out var isNull));
        Assert.False(isNull);
        storage[prefix + 6] = 0x33;
        Assert.Equal((byte)0x33, borrowed[0]);
        Assert.True(row.TryGetContiguousValue(0, out var next, out _));
        Assert.Equal((byte)0x33, next[0]);
        Assert.Equal((byte)0x33, row.GetValue(0)!.Value.FirstSpan[0]);
        Assert.All(storage[..prefix], value => Assert.Equal((byte)0x55, value));
        Assert.All(storage[(prefix + payload.Length)..], value => Assert.Equal((byte)0x55, value));
    }

    [Fact]
    public void ArrayFragmentedMalformedAndSingleColumnRowsCannotReusePreviousBacking()
    {
        var row = new BorrowedRow();
        row.Initialize(Row(Int64(7), Int64(8)).AsMemory(5), 2);
        AssertFields(row, [Int64(7), Int64(8)]);
        var malformed = TestWire.Bytes("0002 00000008 0000000000000009 00000004 00");
        Assert.Throws<InvalidDataException>(() => row.Initialize(malformed.AsMemory(), 2));
        Assert.False(row.IsInitialized);
        Assert.Throws<InvalidOperationException>(() => row.TryGetContiguousValue(0, out _, out _));

        var fragmented = Row(Int64(9), null).AsSpan(5).ToArray();
        row.Initialize(new BackendMessage((byte)'D', BackendMessageKind.DataRow, TestWire.ByteSegments(fragmented), 2));
        Assert.False(row.TryGetContiguousValue(0, out _, out _));
        Assert.Equal(Int64(9), row.GetValue(0)!.Value.ToArray());
        Assert.Null(row.GetValue(1));
        row.Reset();
        Assert.Throws<InvalidOperationException>(() => row.TryGetContiguousValue(0, out _, out _));
        row.Initialize(Row(Int64(10)).AsMemory(5), 1);
        AssertFields(row, [Int64(10)]);
        row.Initialize(Row(Int64(11), [], null).AsMemory(5), 3);
        AssertFields(row, [Int64(11), [], null]);
    }

    [Theory, InlineData(false), InlineData(true)]
    public void CustomMemoryManagerKeepsGetterLifetimeAndNeverRunsArrayExtraction(bool exposesArray)
    {
        var first = Row(Int64(7), [], null).AsSpan(5).ToArray();
        var second = Row(Int64(99), [], null).AsSpan(5).ToArray();
        using var memory = new MutableBytes(first, exposesArray);
        var row = new BorrowedRow();
        row.Initialize(memory.Memory, 3);
        Assert.Equal(0, memory.ArrayExtractions);
        var reads = memory.SpanReads;
        memory.Replace(second);
        Assert.True(row.TryGetContiguousValue(0, out var value, out var isNull));
        Assert.False(isNull);
        Assert.Equal(99, BinaryPrimitives.ReadInt64BigEndian(value));
        Assert.Equal(reads + 1, memory.SpanReads);
        Assert.True(row.TryGetContiguousValue(1, out var empty, out isNull));
        Assert.False(isNull);
        Assert.True(empty.IsEmpty);
        Assert.Equal(reads + 2, memory.SpanReads);
        Assert.True(row.TryGetContiguousValue(2, out var nil, out isNull));
        Assert.True(isNull);
        Assert.True(nil.IsEmpty);
        Assert.Equal(reads + 2, memory.SpanReads);
        Assert.Equal(0, memory.ArrayExtractions);
        memory.Revoke();
        Assert.Throws<ObjectDisposedException>(() => row.TryGetContiguousValue(0, out _, out _));
        row.Reset();
        Assert.Throws<InvalidOperationException>(() => row.TryGetContiguousValue(0, out _, out _));
    }

    private static void AssertFields(BorrowedRow row, byte[]?[] values)
    {
        Assert.Equal(values.Length, row.Count);
        for (var ordinal = 0; ordinal < values.Length; ordinal++)
        {
            Assert.True(row.TryGetContiguousValue(ordinal, out var field, out var isNull));
            Assert.Equal(values[ordinal] is null, isNull);
            if (values[ordinal] is { } expected)
            {
                Assert.Equal(expected, field.ToArray());
                Assert.Equal(expected, row.GetValue(ordinal)!.Value.ToArray());
            }
            else
            {
                Assert.True(field.IsEmpty);
                Assert.Null(row.GetValue(ordinal));
            }
        }
    }

    private sealed class MutableBytes(byte[] bytes, bool exposesArray) : MemoryManager<byte>
    {
        private byte[] _bytes = bytes;
        private bool _revoked;
        internal int SpanReads { get; private set; }
        internal int ArrayExtractions { get; private set; }
        internal void Replace(byte[] replacement)
        {
            Assert.Equal(_bytes.Length, replacement.Length);
            _bytes = replacement;
        }
        internal void Revoke() => _revoked = true;
        public override Span<byte> GetSpan()
        {
            ObjectDisposedException.ThrowIf(_revoked, this);
            SpanReads++;
            return _bytes;
        }
        protected override bool TryGetArray(out ArraySegment<byte> segment)
        {
            ArrayExtractions++;
            segment = exposesArray ? new ArraySegment<byte>(_bytes) : default;
            return exposesArray;
        }
        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) => _revoked = true;
    }
}
