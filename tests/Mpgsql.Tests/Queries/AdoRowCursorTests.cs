using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class AdoRowCursorTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RowsEndingExactlyAtSegmentBoundariesRemainOrderedAcrossRepeatedExecutions()
    {
        // Begin(bigint) is 38 bytes, and each one-field bigint DataRow is 19.
        // This makes each row end exactly at a returned sequence segment boundary.
        await using var wire = new CursorWire(19);
        await using var source = wire.Source();
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand("select cursor boundaries");
        for (var iteration = 0; iteration < 2; iteration++)
        {
            var first = 10L + iteration * 10;
            var opening = command.ExecuteReaderAsync(Token);
            await wire.SyncAsync();
            await wire.WriteAsync(Join(Begin(20), Row(Int64(first)), Row(Int64(first + 1)), Command("SELECT 2"), Ready()));
            await using var reader = await opening.WaitAsync(TestTimeout, Token);
            Assert.True(reader.HasRows);
            foreach (var expected in new[] { first, first + 1 })
            {
                Assert.True(await reader.ReadAsync(Token));
                Assert.Equal(expected, reader.GetFieldValue<long>(0));
                var raw = reader.GetRawValue(0)!.Value;
                Assert.True(raw.IsSingleSegment);
                Assert.Equal(Int64(expected), raw.ToArray());
            }
            Assert.False(await reader.ReadAsync(Token));
            Assert.False(await reader.NextResultAsync(Token));
            Assert.True(wire.Session.IsIdleAndHealthy);
            Assert.Equal(0, wire.Session.BufferedRowBytes);
        }
    }

    [Fact]
    public async Task NoticeAndParameterStatusBetweenCachedRowsAreRoutedExactlyOnce()
    {
        // Keep both rows and the intervening async frames in one physical segment.
        await using var wire = new CursorWire(int.MaxValue);
        await using var source = wire.Source();
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand("select interleaved cursor");
        var opening = command.ExecuteReaderAsync(Token);
        await wire.SyncAsync();
        await wire.WriteAsync(Join(Begin(20), Row(Int64(1)),
            Packet('N', Encoding.UTF8.GetBytes("SNOTICE\0C00000\0Mbetween rows\0\0")),
            Packet('S', Encoding.UTF8.GetBytes("application_name\0cursor_test\0")),
            Row(Int64(2)), Command("SELECT 2"), Ready()));
        await using var reader = await opening.WaitAsync(TestTimeout, Token);
        Assert.True(await reader.ReadAsync(Token));
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.True(await reader.ReadAsync(Token));
        Assert.Equal(2L, reader.GetInt64(0));
        Assert.True(wire.Session.TryReadNotice(out var notice));
        Assert.Equal("between rows", notice.Message);
        Assert.False(wire.Session.TryReadNotice(out _));
        Assert.True(wire.Session.TryGetParameter("application_name", out var value));
        Assert.Equal("cursor_test", value);
        Assert.False(await reader.ReadAsync(Token));
        Assert.False(await reader.NextResultAsync(Token));
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Fact]
    public async Task FragmentedNextRowAfterTwoCachedRowsIsAssembledWithoutReplayingTheirBytes()
    {
        await using var wire = new CursorWire(38);
        await using var source = wire.Source();
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand("select fragmented cursor tail");
        var opening = command.ExecuteReaderAsync(Token);
        await wire.SyncAsync();
        var third = Row(Int64(3));
        await wire.WriteAsync(Join(Begin(20), Row(Int64(1)), Row(Int64(2)), third[..9]));
        await using var reader = await opening.WaitAsync(TestTimeout, Token);
        Assert.True(await reader.ReadAsync(Token));
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.True(await reader.ReadAsync(Token));
        Assert.Equal(2L, reader.GetInt64(0));
        var pending = reader.ReadAsync(Token);
        Assert.False(pending.IsCompleted);
        await wire.WriteAsync(Join(third[9..], Command("SELECT 3"), Ready()));
        Assert.True(await pending.WaitAsync(TestTimeout, Token));
        Assert.Equal(3L, reader.GetFieldValue<long>(0));
        Assert.Equal(Int64(3), reader.GetRawValue(0)!.Value.ToArray());
        Assert.False(await reader.ReadAsync(Token));
        Assert.False(await reader.NextResultAsync(Token));
        Assert.True(wire.Session.IsIdleAndHealthy);
        Assert.Equal(0, wire.Session.BufferedRowBytes);
    }

    [Fact]
    public async Task EarlyAsyncCloseDiscardsCachedTailAndAllowsTheSameCommandToExecuteAgain()
    {
        await using var wire = new CursorWire(38);
        await using var source = wire.Source();
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand("select early cursor close");
        var opening = command.ExecuteReaderAsync(Token);
        await wire.SyncAsync();
        await wire.WriteAsync(Join(Begin(20), Row(Int64(1)), Row(Int64(2)), Command("SELECT 2"), Ready()));
        await using (var reader = await opening.WaitAsync(TestTimeout, Token))
        {
            Assert.True(await reader.ReadAsync(Token));
            Assert.Equal(1L, reader.GetInt64(0));
            await reader.CloseAsync().WaitAsync(TestTimeout, Token);
            Assert.True(reader.IsClosed);
        }
        Assert.True(wire.Session.IsIdleAndHealthy);
        opening = command.ExecuteReaderAsync(Token);
        await wire.SyncAsync();
        await wire.WriteAsync(Join(Query(99), Ready()));
        await using var next = await opening.WaitAsync(TestTimeout, Token);
        Assert.True(await next.ReadAsync(Token));
        Assert.Equal(99L, next.GetInt64(0));
        Assert.False(await next.ReadAsync(Token));
        Assert.False(await next.NextResultAsync(Token));
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Fact]
    public async Task CompletedLastBufferYieldsItsRowsAndReadyButCannotReturnToThePool()
    {
        await using var wire = new CursorWire(19);
        await using var source = wire.Source();
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand("select final cursor buffer");
        var opening = command.ExecuteReaderAsync(Token);
        await wire.SyncAsync();
        await wire.CompleteReplyAsync(Join(Begin(20), Row(Int64(7)), Row(Int64(8)), Command("SELECT 2"), Ready()));
        await using var reader = await opening.WaitAsync(TestTimeout, Token);
        Assert.True(await reader.ReadAsync(Token));
        Assert.Equal(7L, reader.GetInt64(0));
        Assert.True(await reader.ReadAsync(Token));
        Assert.Equal(8L, reader.GetInt64(0));
        Assert.False(await reader.ReadAsync(Token));
        Assert.False(await reader.NextResultAsync(Token));
        Assert.True(wire.Reader.SawCompletedPayload);
        Assert.False(wire.Session.IsIdleAndHealthy);
        Assert.Equal(0, wire.Session.BufferedRowBytes);
    }

    private sealed class CursorWire : IAsyncDisposable
    {
        private readonly Pipe _incoming = new(new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0, useSynchronizationContext: false));
        private readonly Pipe _outgoing = new(new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0, useSynchronizationContext: false));
        internal SegmentingReader Reader { get; }
        internal MpgsqlMessageSession Session { get; }
        internal CursorWire(int segmentSize)
        {
            Reader = new SegmentingReader(_incoming.Reader, segmentSize);
            Session = new MpgsqlMessageSession(Reader, _outgoing.Writer);
        }
        internal MpgsqlDataSource Source() => new(_ => ValueTask.FromResult(Session), (_, _) => ValueTask.CompletedTask);
        internal async Task WriteAsync(byte[] bytes)
            => await _incoming.Writer.WriteAsync(bytes, Token).AsTask().WaitAsync(TestTimeout, Token);
        internal async Task CompleteReplyAsync(byte[] bytes)
        {
            // Publish bytes and IsCompleted atomically, rather than racing a flush
            // against the reader resuming before writer completion becomes visible.
            _incoming.Writer.Write(bytes);
            await _incoming.Writer.CompleteAsync();
        }
        internal async Task SyncAsync()
        {
            var bytes = new List<byte>();
            do
            {
                var read = await _outgoing.Reader.ReadAsync(Token).AsTask().WaitAsync(TestTimeout, Token);
                bytes.AddRange(read.Buffer.ToArray());
                _outgoing.Reader.AdvanceTo(read.Buffer.End);
            }
            while (!Tags([.. bytes]).Contains('S'));
        }
        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync().AsTask().WaitAsync(TestTimeout, Token);
            await _incoming.Writer.CompleteAsync();
            await _outgoing.Reader.CompleteAsync();
        }
    }

    // Re-segment the original memory without copying it. Map consumed/examined
    // positions back to the real pipe, preserving its buffer ownership contract.
    private sealed class SegmentingReader(PipeReader inner, int segmentSize) : PipeReader
    {
        private ReadOnlySequence<byte> _original;
        private ReadOnlySequence<byte> _view;
        internal bool SawCompletedPayload { get; private set; }
        public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
            => View(await inner.ReadAsync(cancellationToken).ConfigureAwait(false));
        public override bool TryRead(out ReadResult result)
        {
            if (!inner.TryRead(out var read)) { result = default; return false; }
            result = View(read);
            return true;
        }
        private ReadResult View(ReadResult read)
        {
            _original = read.Buffer;
            Segment? first = null, last = null;
            foreach (var memory in read.Buffer)
            for (var offset = 0; offset < memory.Length;)
            {
                var length = Math.Min(segmentSize, memory.Length - offset);
                var segment = new Segment(memory.Slice(offset, length));
                if (first is null) first = last = segment;
                else last = last!.Append(segment);
                offset += length;
            }
            _view = first is null ? default : new ReadOnlySequence<byte>(first, 0, last!, last!.Memory.Length);
            SawCompletedPayload |= read.IsCompleted && !_original.IsEmpty;
            return new ReadResult(_view, read.IsCanceled, read.IsCompleted);
        }
        public override void AdvanceTo(SequencePosition consumed) => AdvanceTo(consumed, consumed);
        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
        {
            var originalConsumed = _original.GetPosition(_view.Slice(0, consumed).Length);
            var originalExamined = _original.GetPosition(_view.Slice(0, examined).Length);
            inner.AdvanceTo(originalConsumed, originalExamined);
            _original = _view = default;
        }
        public override void CancelPendingRead() => inner.CancelPendingRead();
        public override void Complete(Exception? exception = null) => inner.Complete(exception);
        public override ValueTask CompleteAsync(Exception? exception = null) => inner.CompleteAsync(exception);
        private sealed class Segment : ReadOnlySequenceSegment<byte>
        {
            internal Segment(ReadOnlyMemory<byte> memory) { Memory = memory; }
            internal Segment Append(Segment segment)
            {
                segment.RunningIndex = RunningIndex + Memory.Length;
                Next = segment;
                return segment;
            }
        }
    }
}
