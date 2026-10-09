using System.Buffers;
using System.IO.Pipelines;
using System.Reflection;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class AdoInputLifetimeTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ExternalHandoffRetainsGeneralTokenAndUsesNoneForAdoReads()
    {
        await using var wire = new LifetimeWire(initiallyAdo: false);
        await wire.Reader.WaitForReadAfterAsync(0).WaitAsync(TestTimeout, Token);
        Assert.Equal(1, wire.Reader.CancelableReads);
        await using var source = wire.CreateSource();
        using var connection = await source.OpenConnectionAsync(Token);
        using var command = connection.CreateCommand("select empty");
        var previous = wire.Reader.ReadCalls;
        var opening = command.ExecuteReaderAsync(Token);
        await wire.Reader.WaitForReadAfterAsync(previous).WaitAsync(TestTimeout, Token);
        Assert.Equal(1, wire.Reader.CancelableReads);
        Assert.True(wire.Reader.NonCancelableReads > 0);
        var writing = wire.WriteAsync(Join(Begin(20), Command("SELECT 0"), Ready()));
        using var reader = await opening.WaitAsync(TestTimeout, Token);
        Assert.False(await reader.ReadAsync(Token));
        Assert.False(await reader.NextResultAsync(Token));
        await writing.WaitAsync(TestTimeout, Token);
        Assert.Equal(1, wire.Reader.CancelableReads);
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task InitiallyAdoLifetimeCancellationWakesPendingNoneReadAndReleasesPartialInput(bool fragment)
    {
        using var lifetime = new CancellationTokenSource();
        await using var wire = new LifetimeWire(initiallyAdo: true, lifetime.Token);
        await using var source = wire.CreateSource();
        using var connection = await source.OpenConnectionAsync(Token);
        using var command = connection.CreateCommand("select pending metadata");
        var opening = command.ExecuteReaderAsync(Token);
        await wire.Reader.WaitForReadAfterAsync(0).WaitAsync(TestTimeout, Token);
        if (fragment)
        {
            var previous = wire.Reader.ReadCalls;
            await wire.WriteAsync(Description(20).AsMemory(0, 3));
            await wire.Reader.WaitForReadAfterAsync(previous).WaitAsync(TestTimeout, Token);
        }
        Assert.Equal(0, wire.Reader.CancelableReads);
        Assert.True(wire.Reader.NonCancelableReads > 0);
        lifetime.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TestTimeout, Token));
        Assert.True(wire.Reader.CancelCalls > 0);
        Assert.False(wire.Session.IsHealthy);
        await source.DisposeAsync().AsTask().WaitAsync(TestTimeout, Token);
        await wire.Session.DisposeAsync();
        Assert.Equal(1, wire.Reader.CompleteCalls);
    }

    [Fact]
    public async Task InputFailureRemainsPrimaryWhenFailCancelsTheSessionLifetime()
    {
        await using var wire = new LifetimeWire(initiallyAdo: true);
        await using var source = wire.CreateSource();
        using var connection = await source.OpenConnectionAsync(Token);
        using var command = connection.CreateCommand("select broken input");
        var opening = command.ExecuteReaderAsync(Token);
        await wire.Reader.WaitForReadAfterAsync(0).WaitAsync(TestTimeout, Token);
        var failure = new IOException("The scripted transport failed.");
        await wire.FailInputAsync(failure);
        var error = await Assert.ThrowsAsync<MpgsqlException>(() => opening.WaitAsync(TestTimeout, Token));
        Assert.Same(failure, error.InnerException);
        Assert.Equal(0, wire.Reader.CancelableReads);
        Assert.False(wire.Session.IsHealthy);
        await source.DisposeAsync().AsTask().WaitAsync(TestTimeout, Token);
        Assert.Equal(1, wire.Reader.CompleteCalls);
    }

    private sealed class LifetimeWire : IAsyncDisposable
    {
        private readonly Pipe _incoming = new(new PipeOptions(pauseWriterThreshold: 32, resumeWriterThreshold: 16, useSynchronizationContext: false));
        private readonly Pipe _outgoing = new(new PipeOptions(pauseWriterThreshold: 0, useSynchronizationContext: false));
        internal TokenReader Reader { get; }
        internal MpgsqlMessageSession Session { get; }

        internal LifetimeWire(bool initiallyAdo, CancellationToken lifetime = default)
        {
            Reader = new TokenReader(_incoming.Reader);
            Reader.IsAdoSession = () => initiallyAdo || Session?.IsAdoSession == true;
            if (initiallyAdo)
            {
                // Exercise OpenAdoAsync's private construction mode with a real Pipe,
                // without requiring a database or exposing another session factory API.
                var constructor = typeof(MpgsqlMessageSession).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                    .Single(candidate => candidate.GetParameters().Length == 5);
                Session = (MpgsqlMessageSession)constructor.Invoke([Reader, _outgoing.Writer, lifetime, null, true]);
            }
            else
                Session = new MpgsqlMessageSession(Reader, _outgoing.Writer, lifetime);
        }

        internal MpgsqlDataSource CreateSource() => new(_ => ValueTask.FromResult(Session), (_, _) => ValueTask.CompletedTask);
        internal async Task WriteAsync(ReadOnlyMemory<byte> bytes) => await _incoming.Writer.WriteAsync(bytes, Token).AsTask().WaitAsync(TestTimeout, Token);
        internal ValueTask FailInputAsync(Exception error) => _incoming.Writer.CompleteAsync(error);

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync().AsTask().WaitAsync(TestTimeout, Token);
            await _incoming.Writer.CompleteAsync();
            await _outgoing.Reader.CompleteAsync();
        }
    }

    private sealed class TokenReader(PipeReader inner) : PipeReader
    {
        private readonly Lock _gate = new();
        private TaskCompletionSource? _readStarted;
        private int _reads, _cancelableReads, _nonCancelableReads, _cancelCalls, _completeCalls;
        internal Func<bool>? IsAdoSession;
        internal int ReadCalls => Volatile.Read(ref _reads);
        internal int CancelableReads => Volatile.Read(ref _cancelableReads);
        internal int NonCancelableReads => Volatile.Read(ref _nonCancelableReads);
        internal int CancelCalls => Volatile.Read(ref _cancelCalls);
        internal int CompleteCalls => Volatile.Read(ref _completeCalls);

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
                // The idle control monitor has its own cold cancellation token.
                // These assertions count general/lifetime input vs ADO operation input.
                if (!cancellationToken.CanBeCanceled || IsAdoSession?.Invoke() != true)
                {
                    _reads++;
                    if (cancellationToken.CanBeCanceled)
                        _cancelableReads++;
                    else
                        _nonCancelableReads++;
                    _readStarted?.TrySetResult();
                    _readStarted = null;
                }
            }
            return inner.ReadAsync(cancellationToken);
        }

        public override void CancelPendingRead() { Interlocked.Increment(ref _cancelCalls); inner.CancelPendingRead(); }
        public override void AdvanceTo(SequencePosition consumed) => inner.AdvanceTo(consumed);
        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) => inner.AdvanceTo(consumed, examined);
        public override bool TryRead(out ReadResult result) => inner.TryRead(out result);
        public override void Complete(Exception? exception = null) { Interlocked.Increment(ref _completeCalls); inner.Complete(exception); }
        public override ValueTask CompleteAsync(Exception? exception = null) { Interlocked.Increment(ref _completeCalls); return inner.CompleteAsync(exception); }
    }
}
