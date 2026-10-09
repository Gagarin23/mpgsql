using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Reflection;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class AdoDirectDisposalTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SourceDisposalWaitsForCancelledDirectReadBeforeCompletingInput()
    {
        await using var wire = new DisposalWire();
        await using var source = wire.CreateSource();
        using var connection = await source.OpenConnectionAsync(Token);
        using var command = connection.CreateCommand("select held row");
        var opening = command.ExecuteReaderAsync(Token);
        await wire.SyncAsync();
        await wire.WriteAsync(Join(Begin(20), Row(Int64(42))));
        using var reader = await opening.WaitAsync(TestTimeout, Token);
        Assert.True(await reader.ReadAsync(Token));
        Assert.Equal(42L, reader.GetInt64(0));

        var previousRead = wire.Reader.ReadCalls;
        var reading = reader.ReadAsync(Token);
        await wire.Reader.WaitForReadAfterAsync(previousRead).WaitAsync(TestTimeout, Token);
        wire.Reader.HoldCancelledRead();
        var disposing = source.DisposeAsync().AsTask();
        try
        {
            await wire.Reader.CancellationHeld.WaitAsync(TestTimeout, Token);
            Assert.False(disposing.IsCompleted);
            Assert.Equal(0, wire.Reader.CompleteCalls);
        }
        finally { wire.Reader.ReleaseCancelledRead(); }

        await disposing.WaitAsync(TestTimeout, Token);
        Assert.NotNull(await Record.ExceptionAsync(() => reading.WaitAsync(TestTimeout, Token)));
        Assert.True(reading.IsCompleted);
        await wire.CompleteInputWriterAsync();
        Assert.False(wire.Session.IsHealthy);
        Assert.ThrowsAny<InvalidOperationException>(() => reader.GetInt64(0));
        Assert.Equal(0, wire.Session.BufferedRowBytes);
        Assert.Equal(1, wire.Reader.CompleteCalls);
        Assert.False(wire.Reader.CompletedDuringRead);
        Assert.All(wire.Pool.Owners, owner => Assert.Equal(1, owner.Disposals));

        await source.DisposeAsync();
        await wire.Session.DisposeAsync();
        Assert.Equal(1, wire.Reader.CompleteCalls);
        Assert.All(wire.Pool.Owners, owner => Assert.Equal(1, owner.Disposals));
    }

    [Fact]
    public async Task SourceDisposalWaitsForFragmentedDrainBeforeReleasingFrameAndInput()
    {
        await using var wire = new DisposalWire();
        await using var source = wire.CreateSource();
        using var connection = await source.OpenConnectionAsync(Token);
        using var command = connection.CreateCommand("select fragmented row");
        var opening = command.ExecuteReaderAsync(Token);
        await wire.SyncAsync();
        await wire.WriteAsync(Join(Begin(20), Row(Int64(42))));
        using var reader = await opening.WaitAsync(TestTimeout, Token);
        Assert.True(await reader.ReadAsync(Token));

        var previousRead = wire.Reader.ReadCalls;
        var reading = reader.ReadAsync(Token);
        await wire.Reader.WaitForReadAfterAsync(previousRead).WaitAsync(TestTimeout, Token);
        previousRead = wire.Reader.ReadCalls;
        // Tag and frame length are complete; the column length itself is split.
        // The normal movement must assemble this prefix before owner recovery starts.
        await wire.WriteAsync(Row(Int64(99)).AsMemory(0, 9));
        await wire.Reader.WaitForReadAfterAsync(previousRead).WaitAsync(TestTimeout, Token);
        var frameOwner = CountAssemblingFrame(wire.Session);

        previousRead = wire.Reader.ReadCalls;
        var closing = connection.CloseAsync();
        await wire.Reader.WaitForReadAfterAsync(previousRead).WaitAsync(TestTimeout, Token);
        Assert.False(closing.IsCompleted);
        Assert.Equal(0, frameOwner.Disposals);
        wire.Reader.HoldCancelledRead();
        var disposing = source.DisposeAsync().AsTask();
        try
        {
            await wire.Reader.CancellationHeld.WaitAsync(TestTimeout, Token);
            Assert.False(disposing.IsCompleted);
            Assert.Equal(0, frameOwner.Disposals);
            Assert.Equal(0, wire.Reader.CompleteCalls);
        }
        finally { wire.Reader.ReleaseCancelledRead(); }

        await disposing.WaitAsync(TestTimeout, Token);
        // Observe both callers. A finish failure is reported once, so the close may
        // succeed after the movement has already observed that failure.
        Assert.NotNull(await Record.ExceptionAsync(() => reading.WaitAsync(TestTimeout, Token)));
        await Record.ExceptionAsync(() => closing.WaitAsync(TestTimeout, Token));
        Assert.True(reading.IsCompleted);
        Assert.True(closing.IsCompleted);
        await wire.CompleteInputWriterAsync();
        Assert.False(wire.Session.IsHealthy);
        Assert.Equal(0, wire.Session.BufferedRowBytes);
        Assert.Equal(1, frameOwner.Disposals);
        Assert.Equal(1, wire.Reader.CompleteCalls);
        Assert.False(wire.Reader.CompletedDuringRead);
        Assert.All(wire.Pool.Owners, owner => Assert.Equal(1, owner.Disposals));

        await source.DisposeAsync();
        await wire.Session.DisposeAsync();
        Assert.Equal(1, frameOwner.Disposals);
        Assert.Equal(1, wire.Reader.CompleteCalls);
    }

    private static CountingOwner CountAssemblingFrame(MpgsqlMessageSession session)
    {
        // The frame assembler uses MemoryPool.Shared. Instrument its existing owner,
        // rather than adding a production allocator hook solely for this lifecycle test.
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var frames = typeof(MpgsqlMessageSession).GetField("_frames", flags)!.GetValue(session)!;
        var field = frames.GetType().GetField("_owner", flags)!;
        var owner = Assert.IsAssignableFrom<IMemoryOwner<byte>>(field.GetValue(frames));
        var counting = new CountingOwner(owner);
        field.SetValue(frames, counting);
        return counting;
    }

    private sealed class CountingOwner(IMemoryOwner<byte> inner) : IMemoryOwner<byte>
    {
        private int _disposals;
        internal int Disposals => Volatile.Read(ref _disposals);
        public Memory<byte> Memory => inner.Memory;
        public void Dispose()
        {
            Interlocked.Increment(ref _disposals);
            inner.Dispose();
        }
    }

    private sealed class DisposalWire : IAsyncDisposable
    {
        internal readonly TrackingPool Pool = new();
        private readonly Pipe _incoming;
        private readonly Pipe _outgoing = new(new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0, useSynchronizationContext: false));
        internal TrackingReader Reader { get; }
        internal MpgsqlMessageSession Session { get; }

        internal DisposalWire()
        {
            _incoming = new Pipe(new PipeOptions(Pool, pauseWriterThreshold: 0, resumeWriterThreshold: 0, useSynchronizationContext: false));
            Reader = new TrackingReader(_incoming.Reader);
            Session = new MpgsqlMessageSession(Reader, _outgoing.Writer);
        }

        internal MpgsqlDataSource CreateSource() => new(
            _ => ValueTask.FromResult(Session), (_, _) => ValueTask.CompletedTask);

        internal async Task WriteAsync(ReadOnlyMemory<byte> bytes)
        {
            await _incoming.Writer.WriteAsync(bytes, Token).AsTask().WaitAsync(TestTimeout, Token);
        }

        internal async Task CompleteInputWriterAsync() => await _incoming.Writer.CompleteAsync();

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
            Reader.ReleaseCancelledRead();
            await Session.DisposeAsync().AsTask().WaitAsync(TestTimeout, Token);
            await _incoming.Writer.CompleteAsync();
            await _outgoing.Reader.CompleteAsync();
            Pool.Dispose();
        }
    }

    private sealed class TrackingReader(PipeReader inner) : PipeReader
    {
        private readonly Lock _gate = new();
        private TaskCompletionSource? _readStarted;
        private TaskCompletionSource? _release;
        private readonly TaskCompletionSource _cancellationHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads, _activeReads, _completeCalls, _completedDuringRead;
        internal int ReadCalls => Volatile.Read(ref _reads);
        internal int CompleteCalls => Volatile.Read(ref _completeCalls);
        internal bool CompletedDuringRead => Volatile.Read(ref _completedDuringRead) != 0;
        internal Task CancellationHeld => _cancellationHeld.Task;

        internal Task WaitForReadAfterAsync(int previous)
        {
            lock (_gate)
                return _reads > previous ? Task.CompletedTask
                    : (_readStarted ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }

        internal void HoldCancelledRead()
        {
            lock (_gate)
                _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        internal void ReleaseCancelledRead()
        {
            lock (_gate)
                _release?.TrySetResult();
        }

        public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _activeReads);
            lock (_gate)
            {
                _reads++;
                _readStarted?.TrySetResult();
                _readStarted = null;
            }
            var cancellationHeld = false;
            try
            {
                var result = await inner.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (result.IsCanceled)
                {
                    cancellationHeld = true;
                    await WaitForCancellationReleaseAsync().ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                return result;
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                // A returned ownership wake and token cancellation can race. Hold
                // this read once, regardless of which cancellation path it takes.
                if (!cancellationHeld)
                    await WaitForCancellationReleaseAsync().ConfigureAwait(false);
                throw;
            }
            finally { Interlocked.Decrement(ref _activeReads); }
        }

        private async Task WaitForCancellationReleaseAsync()
        {
            Task? release;
            lock (_gate) { release = _release?.Task; }
            if (release is not null)
            {
                _cancellationHeld.TrySetResult();
                await release.ConfigureAwait(false);
            }
        }

        public override void AdvanceTo(SequencePosition consumed) => inner.AdvanceTo(consumed);
        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) => inner.AdvanceTo(consumed, examined);
        public override void CancelPendingRead() => inner.CancelPendingRead();
        public override bool TryRead(out ReadResult result) => inner.TryRead(out result);
        public override void Complete(Exception? exception = null)
        {
            ObserveCompletion();
            inner.Complete(exception);
        }
        public override ValueTask CompleteAsync(Exception? exception = null)
        {
            ObserveCompletion();
            return inner.CompleteAsync(exception);
        }
        private void ObserveCompletion()
        {
            Interlocked.Increment(ref _completeCalls);
            if (Volatile.Read(ref _activeReads) != 0)
                Volatile.Write(ref _completedDuringRead, 1);
        }
    }

    private sealed class TrackingPool : MemoryPool<byte>
    {
        internal readonly ConcurrentQueue<PoolOwner> Owners = new();
        public override int MaxBufferSize => int.MaxValue;
        public override IMemoryOwner<byte> Rent(int minBufferSize = -1)
        {
            var owner = new PoolOwner(new byte[Math.Max(minBufferSize, 4096)]);
            Owners.Enqueue(owner);
            return owner;
        }
        protected override void Dispose(bool disposing) { }
    }

    private sealed class PoolOwner(byte[] bytes) : IMemoryOwner<byte>
    {
        private int _disposals;
        internal int Disposals => Volatile.Read(ref _disposals);
        public Memory<byte> Memory => bytes;
        public void Dispose() => Interlocked.Increment(ref _disposals);
    }
}
