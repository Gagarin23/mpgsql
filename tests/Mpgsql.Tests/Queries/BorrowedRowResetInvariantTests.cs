using System.Buffers;
using Mpgsql.Internal;
using Mpgsql.Protocol;
using Mpgsql.Tests.Protocol;

namespace Mpgsql.Tests.Queries;

public sealed class BorrowedRowResetInvariantTests
{
    [Theory, InlineData(false), InlineData(true)]
    public void FailedInitializationAfterValidAndAfterFailureNeverExposesPreviousOrPartialFields(bool fragmented)
    {
        var row = new BorrowedRow();
        var valid = TestWire.Bytes("0002 00000008 0000000000000005 ffffffff");
        var partial = TestWire.Bytes("0002 00000008 0000000000000006 00000004 00");
        var trailing = TestWire.Bytes("0001 00000000 ff");
        Initialize(valid, 2);
        Assert.True(row.IsInitialized);
        Assert.Equal(2, row.Count);
        Assert.Equal(TestWire.Bytes("0000000000000005"), row.GetValue(0)!.Value.ToArray());
        Assert.Null(row.GetValue(1));

        foreach (var (payload, count) in new[] { (partial, 2), (trailing, 1), (Array.Empty<byte>(), 0) })
        {
            Assert.Throws<InvalidDataException>(() => Initialize(payload, count));
            Assert.False(row.IsInitialized);
            Assert.Equal(0, row.Count);
            Assert.Throws<InvalidOperationException>(() => row.GetValue(0));
            Assert.Throws<InvalidOperationException>(() => row.TryGetContiguousValue(0, out _, out _));
        }

        Initialize(valid, 2);
        Assert.True(row.IsInitialized);
        Assert.Equal(2, row.Count);
        Assert.Equal(TestWire.Bytes("0000000000000005"), row.GetValue(0)!.Value.ToArray());
        Assert.Null(row.GetValue(1));
        row.Reset();
        Assert.False(row.IsInitialized);
        Assert.Equal(0, row.Count);
        Initialize(valid, 2);
        Assert.Equal(TestWire.Bytes("0000000000000005"), row.GetValue(0)!.Value.ToArray());

        void Initialize(byte[] payload, int count)
        {
            if (fragmented)
                row.Initialize(new BackendMessage((byte)'D', BackendMessageKind.DataRow, TestWire.ByteSegments(payload), count));
            else
                row.Initialize(payload.AsMemory(), count);
        }
    }
}
