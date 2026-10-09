using System.Buffers;
using System.IO.Pipelines;
using System.Reflection;
using System.Threading.Tasks.Sources;
using Mpgsql.Protocol;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class AdoPrepostedInputTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DisposalBeforeRawReaderRegistrationConsumesThePostedAwaitableBeforeCompletingInput()
    {
        await using var wire = new InputWire();
        await using var source = wire.CreateSource();
        using var connection = await source.OpenConnectionAsync(Token);
        var first = new MpgsqlBatchCommand("select $1::bigint[]");
        using var command = CreateTwoCommandBatch(connection, first);
        using var encoding = new BlockingInputMemory(true);
        first.Parameters.Add(new MpgsqlParameter<ReadOnlyMemory<long>>(TypeOid.Int64Array, encoding.Memory));
        var opening = Task.Run(() => command.ExecuteReaderAsync(Token), Token);
        await encoding.Entered.WaitAsync(TestTimeout, Token);
        Assert.Equal(1, wire.Reader.ReadCalls);
        Assert.Equal(0, wire.Reader.ConsumedReads);
        var disposing = source.DisposeAsync().AsTask();
        try
        {
            await wire.Reader.Cancelled.Task.WaitAsync(TestTimeout, Token);
            Assert.False(disposing.IsCompleted);
            Assert.Equal(0, wire.Reader.ConsumedReads);
            Assert.Equal(0, wire.Reader.CompleteCalls);
        }
        finally { encoding.Resume(); }
        await disposing.WaitAsync(TestTimeout, Token);
        var openingError = await Record.ExceptionAsync(() => opening.WaitAsync(TestTimeout, Token));
        Assert.NotNull(openingError);
        Assert.IsNotType<TimeoutException>(openingError);
        Assert.Equal(1, encoding.Reads);
        encoding.Revoke();
        Assert.Equal(1, wire.Reader.ConsumedReads);
        Assert.Equal(1, wire.Reader.CompleteCalls);
        Assert.False(wire.Reader.CompletedWithUnconsumedRead);
        Assert.False(wire.Session.IsHealthy);
        await source.DisposeAsync();
        await wire.Session.DisposeAsync();
        Assert.Equal(1, wire.Reader.CompleteCalls);
    }

    [Fact]
    public async Task FirstAdvanceManualCancellationConsumesPostedInputAndFollowingExecutionHasNoOldWake()
    {
        await using var wire = new InputWire();
        var cancelSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var source = wire.CreateSource(() => cancelSent.TrySetResult());
        using var connection = await source.OpenConnectionAsync(Token);
        var first = new MpgsqlBatchCommand("select $1");
        using var command = CreateTwoCommandBatch(connection, first);
        first.Parameters.Add(MpgsqlParameter.Int64(7));
        var advances = 0;
        var postedBeforeAdvance = false;
        wire.Writer.AfterAdvance = () =>
        {
            if (++advances != 1)
                return;
            postedBeforeAdvance = wire.Reader.ReadCalls == 1 && wire.Reader.ConsumedReads == 0;
            command.Cancel();
        };
#pragma warning disable xUnit1051
        var opening = command.ExecuteReaderAsync();
#pragma warning restore xUnit1051
        Assert.Equal(Expected(7, includeSecond: false), await wire.ReadOutputAsync());
        Assert.True(postedBeforeAdvance);
        await cancelSent.Task.WaitAsync(TestTimeout, Token);
        var writing = wire.WriteAsync(Join(Error("57014"), Ready()));
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TestTimeout, Token));
        Assert.False(error.CancellationToken.CanBeCanceled);
        await writing.WaitAsync(TestTimeout, Token);
        Assert.Equal(wire.Reader.ReadCalls, wire.Reader.ConsumedReads);
        Assert.True(wire.Session.IsIdleAndHealthy);
        wire.Writer.AfterAdvance = null;
        first.Parameters[0].Value = 9L;
        var next = command.ExecuteReaderAsync(Token);
        Assert.Equal(Expected(9), await wire.ReadOutputAsync());
        var reply = wire.WriteAsync(Join(Query(9), Query(11), Ready()));
        using var reader = await next.WaitAsync(TestTimeout, Token);
        Assert.True(await reader.ReadAsync(Token));
        Assert.Equal(9L, reader.GetInt64(0));
        Assert.False(await reader.ReadAsync(Token));
        await ReadSecondResultAsync(reader);
        await reply.WaitAsync(TestTimeout, Token);
        Assert.Equal(wire.Reader.ReadCalls, wire.Reader.ConsumedReads);
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Theory, InlineData(1), InlineData(int.MaxValue)]
    public async Task PostedInputKeepsFragmentedOrBufferedRowOwnershipThroughCloseDrainAndReuse(int fragment)
    {
        await using var wire = new InputWire();
        await using var source = wire.CreateSource();
        using var connection = await source.OpenConnectionAsync(Token);
        var first = new MpgsqlBatchCommand("select $1");
        using var command = CreateTwoCommandBatch(connection, first);
        first.Parameters.Add(MpgsqlParameter.Int64(7));
        var beforeSpan = (Reads: 0, Consumed: 0);
        wire.Writer.BeforeFirstSpan = () => beforeSpan = (wire.Reader.ReadCalls, wire.Reader.ConsumedReads);
        var opening = command.ExecuteReaderAsync(Token);
        Assert.Equal(Expected(7), await wire.ReadOutputAsync());
        Assert.Equal((1, 0), beforeSpan);
        var writing = wire.WriteAsync(Join(Query(7), Query(11), Ready()), fragment);
        var previous = await opening.WaitAsync(TestTimeout, Token);
        Assert.True(await previous.ReadAsync(Token));
        Assert.Equal(7L, previous.GetInt64(0));
        // Close while positioned: the same input owner drains the command/RFQ,
        // rather than parsing or releasing the posted buffer from a background loop.
        await previous.DisposeAsync().AsTask().WaitAsync(TestTimeout, Token);
        await writing.WaitAsync(TestTimeout, Token);
        Assert.Throws<ObjectDisposedException>(() => previous.GetRawValue(0));
        Assert.Equal(wire.Reader.ReadCalls, wire.Reader.ConsumedReads);
        Assert.True(wire.Session.IsIdleAndHealthy);

        wire.Writer.BeforeFirstSpan = null;
        first.Parameters[0].Value = 9L;
        var next = command.ExecuteReaderAsync(Token);
        Assert.Equal(Expected(9), await wire.ReadOutputAsync());
        var cancels = wire.Reader.CancelCalls;
        await previous.DisposeAsync();
        Assert.Equal(cancels, wire.Reader.CancelCalls);
        var reply = wire.WriteAsync(Join(Query(9), Query(11), Ready()));
        using var reader = await next.WaitAsync(TestTimeout, Token);
        Assert.True(await reader.ReadAsync(Token));
        Assert.Equal(9L, reader.GetInt64(0));
        Assert.False(await reader.ReadAsync(Token));
        await ReadSecondResultAsync(reader);
        await reply.WaitAsync(TestTimeout, Token);
        Assert.Equal(wire.Reader.ReadCalls, wire.Reader.ConsumedReads);
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task PostedInputFailureIsObservedAndStillReleasesWriterAdmission(bool synchronous)
    {
        await using var wire = new InputWire();
        var failure = new IOException("The posted transport read failed.");
        if (synchronous)
            wire.Reader.NextSynchronousFailure = failure;
        await using var source = wire.CreateSource();
        using var connection = await source.OpenConnectionAsync(Token);
        var first = new MpgsqlBatchCommand("select $1");
        using var command = CreateTwoCommandBatch(connection, first);
        first.Parameters.Add(MpgsqlParameter.Int64(7));
        var opening = command.ExecuteReaderAsync(Token);
        if (!synchronous)
        {
            Assert.Equal(Expected(7), await wire.ReadOutputAsync());
            await wire.FailInputAsync(failure);
        }
        var error = await Assert.ThrowsAsync<MpgsqlException>(() => opening.WaitAsync(TestTimeout, Token));
        Assert.Same(failure, error.InnerException);
        // A synchronous input failure can fault the output pipe before its reader
        // observes the packet. Capture every committed byte before completing it.
        Assert.Equal(Expected(7), wire.Writer.CommittedBytes);
        first.Parameters[0].Value = 9L; // Execution and encoder input ownership ended.
        Assert.Equal(1, wire.Reader.ReadCalls);
        Assert.Equal(synchronous ? 0 : 1, wire.Reader.ConsumedReads);
        await source.DisposeAsync().AsTask().WaitAsync(TestTimeout, Token);
        Assert.Equal(1, wire.Reader.CompleteCalls);
        Assert.False(wire.Reader.CompletedWithUnconsumedRead);
        Assert.False(wire.Session.IsHealthy);
    }

    private static MpgsqlBatch CreateTwoCommandBatch(MpgsqlConnection connection, MpgsqlBatchCommand first)
    {
        var batch = connection.CreateBatch();
        batch.BatchCommands.Add(first);
        var second = new MpgsqlBatchCommand("select $1");
        second.Parameters.Add(MpgsqlParameter.Int64(11));
        batch.BatchCommands.Add(second);
        return batch;
    }

    private static async Task ReadSecondResultAsync(MpgsqlDataReader reader)
    {
        Assert.True(await reader.NextResultAsync(Token));
        Assert.True(await reader.ReadAsync(Token));
        Assert.Equal(11L, reader.GetInt64(0));
        Assert.False(await reader.ReadAsync(Token));
        Assert.False(await reader.NextResultAsync(Token));
    }

    private static byte[] Expected(long value, bool includeSecond = true)
    {
        var output = new ArrayBufferWriter<byte>();
        WriteQuery(value);
        if (includeSecond)
            WriteQuery(11);
        FrontendMessage.Sync().Write(output);
        return output.WrittenSpan.ToArray();

        void WriteQuery(long parameter)
        {
            FrontendMessage.Parse("select $1", parameterTypes: new uint[] { 20 }).Write(output);
            FrontendMessage.Bind(parameters: new ReadOnlyMemory<byte>?[] { Int64(parameter) },
                parameterFormats: new[] { FormatCode.Binary }, resultFormats: new[] { FormatCode.Binary }).Write(output);
            FrontendMessage.Describe(StatementOrPortal.Portal).Write(output);
            FrontendMessage.Execute().Write(output);
        }
    }

    private sealed class InputWire : IAsyncDisposable
    {
        private readonly Pipe _incoming = new(new PipeOptions(pauseWriterThreshold: 32, resumeWriterThreshold: 16, useSynchronizationContext: false));
        private readonly Pipe _outgoing = new(new PipeOptions(pauseWriterThreshold: 0, useSynchronizationContext: false));
        internal OwnershipReader Reader { get; }
        internal ObservingWriter Writer { get; }
        internal MpgsqlMessageSession Session { get; }
        internal InputWire()
        {
            Reader = new OwnershipReader(_incoming.Reader);
            Writer = new ObservingWriter(_outgoing.Writer);
            var constructor = typeof(MpgsqlMessageSession).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(candidate => candidate.GetParameters().Length == 5);
            Session = (MpgsqlMessageSession)constructor.Invoke([Reader, Writer, CancellationToken.None, null, true]);
        }
        internal MpgsqlDataSource CreateSource(Action? cancel = null) => new(_ => ValueTask.FromResult(Session), (_, _) =>
        {
            cancel?.Invoke();
            return ValueTask.CompletedTask;
        });
        internal async Task WriteAsync(byte[] bytes, int fragment = int.MaxValue)
        {
            for (var offset = 0; offset < bytes.Length;)
            {
                var size = Math.Min(fragment, bytes.Length - offset);
                await _incoming.Writer.WriteAsync(bytes.AsMemory(offset, size), Token).AsTask().WaitAsync(TestTimeout, Token);
                offset += size;
            }
        }
        internal ValueTask FailInputAsync(Exception error) => _incoming.Writer.CompleteAsync(error);
        internal async Task<byte[]> ReadOutputAsync()
        {
            var read = await _outgoing.Reader.ReadAsync(Token).AsTask().WaitAsync(TestTimeout, Token);
            var bytes = read.Buffer.ToArray();
            _outgoing.Reader.AdvanceTo(read.Buffer.End);
            return bytes;
        }
        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync().AsTask().WaitAsync(TestTimeout, Token);
            await _incoming.Writer.CompleteAsync();
            await _outgoing.Reader.CompleteAsync();
        }
    }

    // A single-use source makes an unawaited or double-awaited prepost observable,
    // including a completed/cancelled operation that otherwise hides ownership bugs.
    private sealed class OwnershipReader(PipeReader inner) : PipeReader
    {
        private int _reads, _consumed, _unconsumed, _cancels, _completions, _completedWithUnconsumed;
        internal Exception? NextSynchronousFailure;
        internal readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ReadCalls => Volatile.Read(ref _reads);
        internal int ConsumedReads => Volatile.Read(ref _consumed);
        internal int CancelCalls => Volatile.Read(ref _cancels);
        internal int CompleteCalls => Volatile.Read(ref _completions);
        internal bool CompletedWithUnconsumedRead => Volatile.Read(ref _completedWithUnconsumed) != 0;
        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            var operationRead = !cancellationToken.CanBeCanceled;
            if (operationRead)
                Interlocked.Increment(ref _reads);
            if (operationRead && NextSynchronousFailure is { } error)
            {
                NextSynchronousFailure = null;
                throw error;
            }
            var pending = inner.ReadAsync(cancellationToken);
            Interlocked.Increment(ref _unconsumed);
            return new ReadAwaitable(this, pending, operationRead).Awaitable;
        }
        private void Consumed(bool operationRead)
        {
            if (operationRead)
                Interlocked.Increment(ref _consumed);
            Interlocked.Decrement(ref _unconsumed);
        }
        public override void CancelPendingRead() { Interlocked.Increment(ref _cancels); inner.CancelPendingRead(); Cancelled.TrySetResult(); }
        public override void AdvanceTo(SequencePosition consumed) => inner.AdvanceTo(consumed);
        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) => inner.AdvanceTo(consumed, examined);
        public override bool TryRead(out ReadResult result) => inner.TryRead(out result);
        private void CompleteObserved()
        {
            Interlocked.Increment(ref _completions);
            if (Volatile.Read(ref _unconsumed) != 0)
                Interlocked.Exchange(ref _completedWithUnconsumed, 1);
        }
        public override void Complete(Exception? exception = null) { CompleteObserved(); inner.Complete(exception); }
        public override ValueTask CompleteAsync(Exception? exception = null) { CompleteObserved(); return inner.CompleteAsync(exception); }

        private sealed class ReadAwaitable : IValueTaskSource<ReadResult>
        {
            private ManualResetValueTaskSourceCore<ReadResult> _source = new() { RunContinuationsAsynchronously = true };
            private readonly OwnershipReader _owner;
            private readonly bool _operationRead;
            private int _consumed;
            internal ReadAwaitable(OwnershipReader owner, ValueTask<ReadResult> pending, bool operationRead)
            {
                _owner = owner;
                _operationRead = operationRead;
                _ = CompleteAsync(pending);
            }
            internal ValueTask<ReadResult> Awaitable => new(this, _source.Version);
            private async Task CompleteAsync(ValueTask<ReadResult> pending)
            {
                try { _source.SetResult(await pending.ConfigureAwait(false)); }
                catch (Exception error) { _source.SetException(error); }
            }
            public ReadResult GetResult(short token)
            {
                if (Interlocked.Exchange(ref _consumed, 1) != 0)
                    throw new InvalidOperationException("The posted input was consumed more than once.");
                try { return _source.GetResult(token); }
                finally { _owner.Consumed(_operationRead); }
            }
            public ValueTaskSourceStatus GetStatus(short token) => _source.GetStatus(token);
            public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
                => _source.OnCompleted(continuation, state, token, flags);
        }
    }

    private sealed class ObservingWriter(PipeWriter inner) : PipeWriter
    {
        private bool _observed;
        private Memory<byte> _memory;
        private readonly ArrayBufferWriter<byte> _committed = new();
        internal byte[] CommittedBytes => _committed.WrittenSpan.ToArray();
        internal Action? BeforeFirstSpan;
        internal Action? AfterAdvance;
        private void Observe()
        {
            if (_observed)
                return;
            _observed = true;
            BeforeFirstSpan?.Invoke();
        }
        public override Memory<byte> GetMemory(int sizeHint = 0)
        {
            Observe();
            return _memory = inner.GetMemory(sizeHint);
        }
        public override Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
        public override void Advance(int bytes)
        {
            _committed.Write(_memory.Span[..bytes]);
            _memory = default;
            inner.Advance(bytes);
            AfterAdvance?.Invoke();
        }
        public override void CancelPendingFlush() => inner.CancelPendingFlush();
        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) => inner.FlushAsync(cancellationToken);
        public override void Complete(Exception? exception = null) => inner.Complete(exception);
        public override ValueTask CompleteAsync(Exception? exception = null) => inner.CompleteAsync(exception);
    }
}
