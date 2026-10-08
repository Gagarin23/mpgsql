using System.Buffers;
using Mpgsql.Protocol;

namespace Mpgsql.Tests.Protocol;

public sealed class BackendHeaderTests
{
    private static bool Read(ref ReadOnlySequence<byte> input, int mode,
        out BackendMessage message,
        out IndexedDataRow row, Memory<ReadOnlySequence<byte>?> fields = default,
        int limit = BackendMessageReader.DefaultMaxMessageLength)
    {
        row = default;
        return mode switch
        {
            0 => BackendMessageReader.TryRead(ref input, out message, limit),
            1 => BackendMessageReader.TryRead(ref input, fields, out message, out row, limit),
            2 => BackendMessageReader.TryReadForSession(ref input, out message),
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
    }

    [Theory, InlineData(0), InlineData(1), InlineData(2)]
    public void NonzeroStartAndEveryHeaderSplitPreserveFrameAndFollowingBytes(int mode)
    {
        var frame = TestWire.Bytes("44 0000001a 0003 00000008 0102030405060708 ffffffff 00000000");
        var next = TestWire.Bytes("5a 00000005 49");
        byte[] data = [99, 98, 97, .. frame, .. next];
        for (var split = 0; split <= frame.Length; split++)
        {
            // Including split=0/5/full exercises empty first segments and both header paths.
            var input = TestWire.Chunks(data.AsMemory(0, 3 + split), ReadOnlyMemory<byte>.Empty,
                data.AsMemory(3 + split)).Slice(3);
            var fields = new ReadOnlySequence<byte>?[3];
            Assert.True(Read(ref input, mode, out var message, out var row, fields));
            Assert.Equal(BackendMessageKind.DataRow, message.Kind);
            Assert.Equal(frame[5..], message.Payload.ToArray());
            Assert.Equal(3, message.GetDataRow().Count);
            if (mode == 1)
            {
                Assert.Equal(TestWire.Bytes("0102030405060708"), row.Values.Span[0]!.Value.ToArray());
                Assert.Null(row.Values.Span[1]);
                Assert.True(row.Values.Span[2]!.Value.IsEmpty);
            }
            Assert.Equal(next, input.ToArray());
            Assert.True(Read(ref input, mode, out var ready, out _, fields));
            Assert.Equal(TransactionStatus.Idle, ready.GetTransactionStatus());
            Assert.True(input.IsEmpty);
        }
    }

    [Theory, InlineData(0), InlineData(1), InlineData(2)]
    public void EmptyControlPayloadEndConsumesOnlyItsFrame(int mode)
    {
        var frames = TestWire.Bytes("31 00000004 32 00000004 5a 00000005 49");
        byte[] data = [17, 18, .. frames];
        foreach (var fragmented in new[] {false, true})
        {
            var input = (fragmented ? TestWire.ByteSegments(data) : TestWire.Chunks(data.AsMemory())).Slice(2);
            Assert.True(Read(ref input, mode, out var parse, out _));
            Assert.Equal(BackendMessageKind.ParseComplete, parse.Kind);
            Assert.True(parse.Payload.IsEmpty);
            Assert.Equal(frames[5..], input.ToArray());
            Assert.True(Read(ref input, mode, out var bind, out _));
            Assert.Equal(BackendMessageKind.BindComplete, bind.Kind);
            Assert.True(bind.Payload.IsEmpty);
            Assert.Equal(frames[10..], input.ToArray());
            Assert.True(Read(ref input, mode, out var ready, out _));
            Assert.Equal(TransactionStatus.Idle, ready.GetTransactionStatus());
            Assert.True(input.IsEmpty);
        }
    }

    [Theory, InlineData(0), InlineData(1), InlineData(2)]
    public void EveryContiguousTruncationLeavesInputUnconsumed(int mode)
    {
        var frame = TestWire.Bytes("44 00000012 0001 00000008 0102030405060708");
        for (var length = 0; length < frame.Length; length++)
        {
            var input = new ReadOnlySequence<byte>(frame.AsMemory(0, length));
            var before = input;
            Assert.False(Read(ref input, mode, out var message, out _, new ReadOnlySequence<byte>?[1]));
            Assert.Equal(before.Start, input.Start);
            Assert.Equal(before.End, input.End);
            Assert.Equal(before.ToArray(), input.ToArray());
            Assert.True(message.Payload.IsEmpty);
        }
    }

    [Theory, InlineData(0), InlineData(1), InlineData(2)]
    public void InvalidLengthIsRejectedImmediatelyOnBothHeaderPaths(int mode)
    {
        foreach (var hex in new[] {"43 00000003", "43 ffffffff", "43 7fffffff"})
        foreach (var fragmented in new[] {false, true})
        {
            var bytes = TestWire.Bytes(hex);
            var input = fragmented ? TestWire.ByteSegments(bytes) : new ReadOnlySequence<byte>(bytes);
            var before = input;
            Assert.Throws<InvalidDataException>(() => Read(ref input, mode, out _, out _));
            Assert.Equal(before.Start, input.Start);
            Assert.Equal(before.End, input.End);
        }
    }

    [Theory, InlineData(0), InlineData(1)]
    public void ContiguousHeaderDoesNotRelaxBodyValidationOrExplicitLimit(int mode)
    {
        foreach (var hex in new[] {"31 00000005 01", "5a 00000005 58", "43 00000007 780001"})
        {
            var input = new ReadOnlySequence<byte>(TestWire.Bytes(hex));
            var before = input;
            Assert.Throws<InvalidDataException>(() => Read(ref input, mode, out _, out _));
            Assert.Equal(before.Start, input.Start);
            Assert.Equal(before.End, input.End);
        }
        var bounded = new ReadOnlySequence<byte>(TestWire.Bytes("64 00000006 0102"));
        var original = bounded;
        Assert.Throws<InvalidDataException>(() => Read(ref bounded, mode, out _, out _, limit: 5));
        Assert.Equal(original.Start, bounded.Start);
        Assert.Equal(original.End, bounded.End);
        Assert.True(Read(ref bounded, mode, out var message, out _, limit: 6));
        Assert.Equal(TestWire.Bytes("0102"), message.GetCopyData().ToArray());
        Assert.True(bounded.IsEmpty);
    }
}