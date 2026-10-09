using System.Buffers;
using System.IO.Pipelines;
using System.Reflection;
using Mpgsql.Protocol;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class AdoInputAvailabilityTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory, InlineData(false), InlineData(true)]
    public async Task ReadyInCompletedInputEndsTheOperationBeforeIdleEof(bool trailingNotice)
    {
        await using var wire = new AvailabilityWire();
        await using var source = wire.CreateSource();
        using var connection = await source.OpenConnectionAsync(Token);
        using var command = connection.CreateCommand("select $1");
        command.Parameters.Add(MpgsqlParameter.Int64(7));
        var opening = command.ExecuteReaderAsync(Token);
        Assert.Equal(Expected(7), await wire.ReadOutputAsync());
        await wire.Reader.WaitForOperationReadAfterAsync(0).WaitAsync(TestTimeout, Token);
        await wire.CompleteInputAsync(trailingNotice
            ? Join(Query(7), Ready(), Packet('N', "SNOTICE\0Mafter ready\0\0"u8.ToArray()))
            : Join(Query(7), Ready()));
        using var reader = await opening.WaitAsync(TestTimeout, Token);
        Assert.True(await reader.ReadAsync(Token));
        Assert.Equal(7L, reader.GetInt64(0));
        Assert.False(await reader.ReadAsync(Token));
        Assert.False(await reader.NextResultAsync(Token));
        Assert.Equal(1, wire.Reader.OperationReads);
        Assert.Equal(1, wire.Reader.CompletedOperationInputs);
        Assert.Equal(0, wire.Reader.DrainReads);
        // RFQ releases the operation; a later idle owner observes the actual EOF
        // and any trailing control message without reading past RFQ in that operation.
        await Assert.ThrowsAsync<EndOfStreamException>(() => wire.Session.Completion.WaitAsync(TestTimeout, Token));
        if (trailingNotice)
        {
            Assert.True(wire.Session.TryReadNotice(out var notice));
            Assert.Equal("after ready", notice.Message);
        }
        Assert.Equal(1, wire.Reader.OperationReads);
        Assert.False(wire.Reader.CompletedDuringPendingRead);
    }

    [Fact]
    public async Task CompletedPartialFramePreservesTheOriginalEofFailure()
    {
        await using var wire = new AvailabilityWire();
        await using var source = wire.CreateSource();
        using var connection = await source.OpenConnectionAsync(Token);
        using var command = connection.CreateCommand("select $1");
        command.Parameters.Add(MpgsqlParameter.Int64(7));
        var opening = command.ExecuteReaderAsync(Token);
        Assert.Equal(Expected(7), await wire.ReadOutputAsync());
        await wire.CompleteInputAsync(Join(Begin(20), Row(Int64(7))[..9]));
        var operationError = await Assert.ThrowsAsync<MpgsqlException>(() => opening.WaitAsync(TestTimeout, Token));
        var eof = Assert.IsType<EndOfStreamException>(operationError.InnerException);
        Assert.Equal("Truncated backend frame.", eof.Message);
        var sessionError = await Assert.ThrowsAsync<EndOfStreamException>(
            () => wire.Session.Completion.WaitAsync(TestTimeout, Token));
        Assert.Same(eof, sessionError);
        Assert.Equal(1, wire.Reader.OperationReads);
        Assert.False(wire.Session.IsHealthy);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task CancelWakeDoesNotPostAnotherOperationReadBeforeRecovery(bool useToken)
    {
        await using var wire = new AvailabilityWire();
        var cancelSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancels = 0;
        await using var source = wire.CreateSource(() =>
        {
            Interlocked.Increment(ref cancels);
            cancelSent.TrySetResult();
        });
        using var connection = await source.OpenConnectionAsync(Token);
        using var command = connection.CreateCommand("select $1");
        command.Parameters.Add(MpgsqlParameter.Int64(7));
        using var request = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var opening = command.ExecuteReaderAsync(request.Token);
        Assert.Equal(Expected(7), await wire.ReadOutputAsync());
        await wire.Reader.WaitForOperationReadAfterAsync(0).WaitAsync(TestTimeout, Token);
        var before = wire.Reader.OperationReads;
        if (useToken) request.Cancel();
        else command.Cancel();
        await wire.Reader.CancelWake.Task.WaitAsync(TestTimeout, Token);
        await cancelSent.Task.WaitAsync(TestTimeout, Token);
        // The next input read belongs to the recovery drain, not to the stopped
        // movement. Keep the server response pending until that ownership is visible.
        await wire.Reader.WaitForDrainReadAsync().WaitAsync(TestTimeout, Token);
        Assert.Equal(before, wire.Reader.OperationReads);
        var writing = wire.WriteAsync(Join(Error("57014"), Ready()));
        var cancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => opening.WaitAsync(TestTimeout, Token));
        Assert.Equal(request.Token, cancellation.CancellationToken);
        await writing.WaitAsync(TestTimeout, Token);
        Assert.Equal(before, wire.Reader.OperationReads);
        Assert.Equal(1, cancels);
        Assert.True(wire.Session.IsIdleAndHealthy);
        command.Parameters[0].Value = 9L;
        await ReadValueAsync(wire, command, 9);
        Assert.Equal(1, cancels);
    }

    [Theory, InlineData(1), InlineData(int.MaxValue)]
    public async Task AsynchronousOnlyInputRetriesUntilTheResultArrives(int fragment)
    {
        await using var wire = new AvailabilityWire();
        await using var source = wire.CreateSource();
        using var connection = await source.OpenConnectionAsync(Token);
        using var command = connection.CreateCommand("select $1");
        command.Parameters.Add(MpgsqlParameter.Int64(7));
        var opening = command.ExecuteReaderAsync(Token);
        Assert.Equal(Expected(7), await wire.ReadOutputAsync());
        await wire.Reader.WaitForOperationReadAfterAsync(0).WaitAsync(TestTimeout, Token);
        var before = wire.Reader.OperationReads;
        await wire.WriteAsync(Join(Packet('N', "SNOTICE\0Mwhile waiting\0\0"u8.ToArray()),
            Packet('A', [0, 0, 0, 7, .. "events\0waiting\0"u8.ToArray()]),
            Packet('S', "availability_test\0seen\0"u8.ToArray())), fragment);
        await WaitForParameterAsync(wire.Session);
        await wire.Reader.WaitForOperationReadAfterAsync(before).WaitAsync(TestTimeout, Token);
        Assert.False(opening.IsCompleted);
        Assert.True(wire.Session.TryGetParameter("availability_test", out var status));
        Assert.Equal("seen", status);
        Assert.True(wire.Session.TryReadNotice(out var notice));
        Assert.Equal("while waiting", notice.Message);
        Assert.True(wire.Session.TryReadNotification(out var notification));
        Assert.Equal("waiting", notification.Payload);
        var writing = wire.WriteAsync(Join(Query(7), Ready()), fragment);
        using var reader = await opening.WaitAsync(TestTimeout, Token);
        Assert.True(reader.HasRows);
        Assert.True(await reader.ReadAsync(Token));
        Assert.Equal(7L, reader.GetInt64(0));
        Assert.False(await reader.ReadAsync(Token));
        Assert.False(await reader.NextResultAsync(Token));
        await writing.WaitAsync(TestTimeout, Token);
        Assert.True(wire.Session.IsIdleAndHealthy);
        command.Parameters[0].Value = 9L;
        await ReadValueAsync(wire, command, 9);
    }

    [Fact]
    public async Task InputOnlyWaitPublishesTheOriginalTransportFailure()
    {
        await using var wire = new AvailabilityWire();
        await using var source = wire.CreateSource();
        using var connection = await source.OpenConnectionAsync(Token);
        using var command = connection.CreateCommand("select $1");
        command.Parameters.Add(MpgsqlParameter.Int64(7));
        var opening = command.ExecuteReaderAsync(Token);
        Assert.Equal(Expected(7), await wire.ReadOutputAsync());
        await wire.Reader.WaitForOperationReadAfterAsync(0).WaitAsync(TestTimeout, Token);
        var failure = new IOException("Original input-only transport failure.");
        await wire.CompleteInputAsync([], failure);
        var operationError = await Assert.ThrowsAsync<MpgsqlException>(() => opening.WaitAsync(TestTimeout, Token));
        Assert.Same(failure, operationError.InnerException);
        var sessionError = await Assert.ThrowsAsync<IOException>(
            () => wire.Session.Completion.WaitAsync(TestTimeout, Token));
        Assert.Same(failure, sessionError);
        Assert.False(wire.Reader.CompletedDuringPendingRead);
    }

    private static async Task ReadValueAsync(AvailabilityWire wire, MpgsqlCommand command, long value)
    {
        var opening = command.ExecuteReaderAsync(Token);
        Assert.Equal(Expected(value), await wire.ReadOutputAsync());
        var writing = wire.WriteAsync(Join(Query(value), Ready()));
        using var reader = await opening.WaitAsync(TestTimeout, Token);
        Assert.True(await reader.ReadAsync(Token));
        Assert.Equal(value, reader.GetInt64(0));
        Assert.False(await reader.ReadAsync(Token));
        Assert.False(await reader.NextResultAsync(Token));
        await writing.WaitAsync(TestTimeout, Token);
    }

    private static async Task WaitForParameterAsync(MpgsqlMessageSession session)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TestTimeout);
        while (!session.TryGetParameter("availability_test", out var value) || value != "seen")
            await Task.Delay(1, deadline.Token);
    }

    private static byte[] Expected(long value)
    {
        var bytes = new ArrayBufferWriter<byte>();
        FrontendMessage.Parse("select $1", parameterTypes: new uint[] { 20 }).Write(bytes);
        FrontendMessage.Bind(parameters: new ReadOnlyMemory<byte>?[] { Int64(value) },
            parameterFormats: new[] { FormatCode.Binary }, resultFormats: new[] { FormatCode.Binary }).Write(bytes);
        FrontendMessage.Describe(StatementOrPortal.Portal).Write(bytes);
        FrontendMessage.Execute().Write(bytes);
        FrontendMessage.Sync().Write(bytes);
        return bytes.WrittenSpan.ToArray();
    }

    private sealed class AvailabilityWire : IAsyncDisposable
    {
        private readonly Pipe _incoming = new(new PipeOptions(pauseWriterThreshold: 32, resumeWriterThreshold: 16, useSynchronizationContext: false));
        private readonly Pipe _outgoing = new(new PipeOptions(pauseWriterThreshold: 0, useSynchronizationContext: false));
        internal CountingReader Reader { get; }
        internal MpgsqlMessageSession Session { get; }
        internal AvailabilityWire()
        {
            Reader = new CountingReader(_incoming.Reader);
            var constructor = typeof(MpgsqlMessageSession).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(candidate => candidate.GetParameters().Length == 5);
            Session = (MpgsqlMessageSession)constructor.Invoke([Reader, _outgoing.Writer, Token, null, true]);
            Reader.Session = Session;
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
        internal ValueTask CompleteInputAsync(byte[] bytes, Exception? failure = null)
        {
            // Completing commits the full final buffer with IsCompleted=true; no
            // blocked flush is awaited while the reader borrows its first row.
            _incoming.Writer.Write(bytes);
            return _incoming.Writer.CompleteAsync(failure);
        }
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

    private sealed class CountingReader(PipeReader inner) : PipeReader
    {
        private static readonly FieldInfo Draining = typeof(MpgsqlMessageSession)
            .GetField("_adoDraining", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private readonly Lock _gate = new();
        private TaskCompletionSource? _changed;
        private int _operationReads, _drainReads, _pendingOperation, _pendingDrain, _pending;
        private int _completedOperationInputs;
        internal MpgsqlMessageSession Session { private get; set; } = null!;
        internal TaskCompletionSource CancelWake { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool CompletedDuringPendingRead;
        internal int OperationReads => Volatile.Read(ref _operationReads);
        internal int DrainReads => Volatile.Read(ref _drainReads);
        internal int CompletedOperationInputs => Volatile.Read(ref _completedOperationInputs);
        internal Task WaitForOperationReadAfterAsync(int previous) => WaitForReadAsync(previous, drain: false);
        internal Task WaitForDrainReadAsync() => WaitForReadAsync(0, drain: true);
        private async Task WaitForReadAsync(int previous, bool drain)
        {
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    if (drain ? _drainReads > previous && _pendingDrain != 0
                        : _operationReads > previous && _pendingOperation != 0)
                        return;
                    changed = (_changed ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
                }
                await changed.ConfigureAwait(false);
            }
        }
        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            var operation = !cancellationToken.CanBeCanceled;
            // The fixture classifies network reads only; this observation never
            // participates in provider parsing, ownership or cancellation decisions.
            var drain = operation && (bool)Draining.GetValue(Session)!;
            Interlocked.Increment(ref _pending);
            var reading = inner.ReadAsync(cancellationToken);
            lock (_gate)
            {
                if (operation)
                {
                    if (drain) { _drainReads++; _pendingDrain++; }
                    else { _operationReads++; _pendingOperation++; }
                }
                _changed?.TrySetResult();
                _changed = null;
            }
            return ObserveAsync(reading, operation, drain);
        }
        private async ValueTask<ReadResult> ObserveAsync(ValueTask<ReadResult> reading, bool operation, bool drain)
        {
            try
            {
                var result = await reading.ConfigureAwait(false);
                if (operation && !drain)
                {
                    if (result.IsCompleted) Interlocked.Increment(ref _completedOperationInputs);
                    if (result.IsCanceled) CancelWake.TrySetResult();
                }
                return result;
            }
            finally
            {
                Interlocked.Decrement(ref _pending);
                lock (_gate)
                {
                    if (operation)
                    {
                        if (drain) _pendingDrain--;
                        else _pendingOperation--;
                    }
                    _changed?.TrySetResult();
                    _changed = null;
                }
            }
        }
        public override void AdvanceTo(SequencePosition consumed) => inner.AdvanceTo(consumed);
        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) => inner.AdvanceTo(consumed, examined);
        public override bool TryRead(out ReadResult result) => inner.TryRead(out result);
        public override void CancelPendingRead() => inner.CancelPendingRead();
        public override void Complete(Exception? exception = null)
        {
            CompletedDuringPendingRead |= Volatile.Read(ref _pending) != 0;
            inner.Complete(exception);
        }
        public override ValueTask CompleteAsync(Exception? exception = null)
        {
            CompletedDuringPendingRead |= Volatile.Read(ref _pending) != 0;
            return inner.CompleteAsync(exception);
        }
    }
}
