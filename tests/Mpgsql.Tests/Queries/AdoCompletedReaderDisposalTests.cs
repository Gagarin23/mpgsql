using System.Buffers;
using System.IO.Pipelines;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class AdoCompletedReaderDisposalTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AsyncConnectionCloseFindsReaderWaitingForFirstMetadataAndRecoversBeforeReuse()
    {
        await using var wire = new CompletionWire();
        await using var source = wire.CreateSource();
        using var connection = await source.OpenConnectionAsync(Token);
        using var command = connection.CreateCommand("select pending first metadata");
        var previousReads = wire.Reader.ReadCalls;
        var opening = command.ExecuteReaderAsync(Token);
        await wire.SyncAsync();
        await wire.Reader.WaitForReadAfterAsync(previousReads).WaitAsync(TestTimeout, Token);
        Assert.False(opening.IsCompleted);

        var closing = connection.CloseAsync();
        Assert.False(closing.IsCompleted);
        // No description has reached the initializing reader. Closing must find
        // its batch registration, invalidate it, and drain through the same RFQ.
        await wire.WriteAsync(Join(Query(7), Ready()));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => opening.WaitAsync(TestTimeout, Token));
        await closing.WaitAsync(TestTimeout, Token);
        Assert.Equal(0, wire.Reader.CompleteCalls);
        Assert.True(wire.Session.IsIdleAndHealthy);
        Assert.Equal(0, wire.Session.BufferedRowBytes);

        await connection.OpenAsync(Token);
        Assert.Same(wire.Session, connection.Session);
        command.CommandText = "select recovered connection";
        var nextOpening = command.ExecuteReaderAsync(Token);
        await wire.SyncAsync();
        await wire.WriteAsync(Join(Query(9), Ready()));
        await using var reader = await nextOpening.WaitAsync(TestTimeout, Token);
        Assert.True(await reader.ReadAsync(Token));
        Assert.Equal(9L, reader.GetInt64(0));
        Assert.False(await reader.ReadAsync(Token));
        Assert.False(await reader.NextResultAsync(Token).WaitAsync(TestTimeout, Token));
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Fact]
    public async Task DisposingCompletedOldReaderDoesNotWakeNextExecutionPendingRead()
    {
        await using var wire = new CompletionWire();
        await using var source = wire.CreateSource();
        using var connection = await source.OpenConnectionAsync(Token);
        using var first = connection.CreateCommand("select previous owner");
        var opening = first.ExecuteReaderAsync(Token);
        await wire.SyncAsync();
        await wire.WriteAsync(Join(Query(7), Ready()));
        await using var previous = await opening.WaitAsync(TestTimeout, Token);
        Assert.True(await previous.ReadAsync(Token));
        Assert.Equal(7L, previous.GetInt64(0));
        Assert.False(await previous.ReadAsync(Token));
        Assert.False(await previous.NextResultAsync(Token).WaitAsync(TestTimeout, Token));
        Assert.True(wire.Session.IsIdleAndHealthy);

        using var next = connection.CreateCommand("select next owner");
        var previousReads = wire.Reader.ReadCalls;
        var nextOpening = next.ExecuteReaderAsync(Token);
        await wire.SyncAsync();
        await wire.Reader.WaitForReadAfterAsync(previousReads).WaitAsync(TestTimeout, Token);
        Assert.False(nextOpening.IsCompleted);
        var cancellationCalls = wire.Reader.CancelCalls;
        await previous.DisposeAsync();
        await previous.CloseAsync();
        Assert.True(previous.IsClosed);
        Assert.Throws<ObjectDisposedException>(() => previous.GetRawValue(0));
        Assert.Equal(cancellationCalls, wire.Reader.CancelCalls);
        Assert.False(nextOpening.IsCompleted);

        await wire.WriteAsync(Join(Query(9), Ready()));
        await using var reader = await nextOpening.WaitAsync(TestTimeout, Token);
        Assert.True(await reader.ReadAsync(Token));
        Assert.Equal(9L, reader.GetInt64(0));
        Assert.False(await reader.ReadAsync(Token));
        Assert.False(await reader.NextResultAsync(Token).WaitAsync(TestTimeout, Token));
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Fact]
    public async Task DisposalAfterObservedServerFailureDoesNotWakeOrReportSameFinishFailureAgain()
    {
        await using var wire = new CompletionWire();
        await using var source = wire.CreateSource();
        using var connection = await source.OpenConnectionAsync(Token);
        using var command = connection.CreateCommand("select then fail");
        var opening = command.ExecuteReaderAsync(Token);
        await wire.SyncAsync();
        await wire.WriteAsync(Join(Begin(20), Row(Int64(7))));
        await using var reader = await opening.WaitAsync(TestTimeout, Token);
        Assert.True(await reader.ReadAsync(Token));
        var movement = reader.ReadAsync(Token);
        await wire.WriteAsync(Join(Error("22012"), Ready()));
        var failure = await Assert.ThrowsAsync<MpgsqlPostgresException>(() => movement.WaitAsync(TestTimeout, Token));
        Assert.Equal("22012", failure.SqlState);
        var cancellationCalls = wire.Reader.CancelCalls;
        await reader.DisposeAsync();
        await reader.CloseAsync();
        Assert.Equal(cancellationCalls, wire.Reader.CancelCalls);
        Assert.True(reader.IsClosed);
        Assert.Throws<ObjectDisposedException>(() => reader.GetRawValue(0));
        Assert.True(wire.Session.IsIdleAndHealthy);
        command.CommandText = "reusable after observed server error";
    }

    private sealed class CompletionWire : IAsyncDisposable
    {
        private readonly Pipe _incoming = new(new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0, useSynchronizationContext: false));
        private readonly Pipe _outgoing = new(new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0, useSynchronizationContext: false));
        internal CountingReader Reader { get; }
        internal MpgsqlMessageSession Session { get; }

        internal CompletionWire()
        {
            Reader = new CountingReader(_incoming.Reader);
            Session = new MpgsqlMessageSession(Reader, _outgoing.Writer);
        }
        internal MpgsqlDataSource CreateSource() => new(_ => ValueTask.FromResult(Session), (_, _) => ValueTask.CompletedTask);

        internal async Task WriteAsync(ReadOnlyMemory<byte> bytes)
            => await _incoming.Writer.WriteAsync(bytes, Token).AsTask().WaitAsync(TestTimeout, Token);

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

    private sealed class CountingReader(PipeReader inner) : PipeReader
    {
        private readonly Lock _gate = new();
        private TaskCompletionSource? _readStarted;
        private int _reads, _cancels, _completions;
        internal int ReadCalls => Volatile.Read(ref _reads);
        internal int CancelCalls => Volatile.Read(ref _cancels);
        internal int CompleteCalls => Volatile.Read(ref _completions);

        internal Task WaitForReadAfterAsync(int previous)
        {
            lock (_gate)
                return _reads > previous ? Task.CompletedTask
                    : (_readStarted ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _reads++;
                _readStarted?.TrySetResult();
                _readStarted = null;
            }
            return inner.ReadAsync(cancellationToken);
        }
        public override void CancelPendingRead()
        {
            Interlocked.Increment(ref _cancels);
            inner.CancelPendingRead();
        }
        public override void AdvanceTo(SequencePosition consumed) => inner.AdvanceTo(consumed);
        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) => inner.AdvanceTo(consumed, examined);
        public override bool TryRead(out ReadResult result) => inner.TryRead(out result);
        public override void Complete(Exception? exception = null)
        {
            Interlocked.Increment(ref _completions);
            inner.Complete(exception);
        }
        public override ValueTask CompleteAsync(Exception? exception = null)
        {
            Interlocked.Increment(ref _completions);
            return inner.CompleteAsync(exception);
        }
    }
}
