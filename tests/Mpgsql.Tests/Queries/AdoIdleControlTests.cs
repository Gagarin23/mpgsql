using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Mpgsql.Protocol;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class AdoIdleControlTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory, InlineData(1), InlineData(int.MaxValue)]
    public async Task IdleAsynchronousMessagesAreObservedWithoutAnOperation(int fragment)
    {
        await using var wire = new IdleWire();
        await wire.Reader.WaitForReadAfterAsync(0).WaitAsync(TestTimeout, Token);
        var writing = wire.WriteAsync(Join(Packet('N', "SNOTICE\0Mhello idle\0\0"u8.ToArray()),
            Packet('A', [0, 0, 0, 7, .. "events\0ready\0"u8.ToArray()]),
            Packet('S', "idle_test\0observed\0"u8.ToArray())), fragment);
        await WaitUntilAsync(() => wire.Session.TryGetParameter("idle_test", out var value) && value == "observed");
        await writing.WaitAsync(TestTimeout, Token);
        Assert.True(wire.Session.TryReadNotice(out var notice));
        Assert.Equal("hello idle", notice.Message);
        Assert.True(wire.Session.TryReadNotification(out var notification));
        Assert.Equal(7, notification.ProcessId);
        Assert.Equal("events", notification.Channel);
        Assert.Equal("ready", notification.Payload);
        Assert.False(wire.Session.Completion.IsCompleted);
        Assert.True(wire.Session.IsHealthy);
        Assert.Equal(0, wire.Reader.ConcurrentReadFailures);
    }

    [Theory]
    [InlineData("FATAL", 1), InlineData("FATAL", int.MaxValue)]
    [InlineData("PANIC", 1), InlineData("PANIC", int.MaxValue)]
    public async Task IdleTerminalDiagnosticsCompleteWithoutEofAndKeepNoQueryAttribution(string severity, int fragment)
    {
        await using var wire = new IdleWire();
        await wire.Reader.WaitForReadAfterAsync(0).WaitAsync(TestTimeout, Token);
        var writing = wire.PublishDiagnosticAsync(Diagnostic(severity), fragment);
        var error = await Assert.ThrowsAsync<MpgsqlServerException>(
            () => wire.Session.Completion.WaitAsync(TestTimeout, Token));
        await writing.WaitAsync(TestTimeout, Token);
        AssertDiagnostic(error, severity);
        Assert.False(wire.Session.IsHealthy);
        Assert.Same(error, Assert.Throws<MpgsqlServerException>(() => wire.Session.CreateBatch(Token)));
        Assert.Equal(0, wire.Reader.ConcurrentReadFailures);
    }

    [Fact]
    public async Task AlreadyObservedFatalPreventsPacketPublicationAndBorrowedParameterEncoding()
    {
        await using var wire = new IdleWire();
        await using var source = wire.CreateSource();
        using var connection = await source.OpenConnectionAsync(Token);
        using var command = connection.CreateCommand("select $1::bigint[]");
        using var input = new BlockingInputMemory();
        command.Parameters.Add(new MpgsqlParameter<ReadOnlyMemory<long>>(TypeOid.Int64Array, input.Memory));
        await wire.Reader.WaitForReadAfterAsync(0).WaitAsync(TestTimeout, Token);
        var writing = wire.PublishDiagnosticAsync(Diagnostic());
        AssertDiagnostic(await Assert.ThrowsAsync<MpgsqlServerException>(
            () => wire.Session.Completion.WaitAsync(TestTimeout, Token)));
        await writing.WaitAsync(TestTimeout, Token);
        // The already broken connection rejects a new execution through its
        // existing availability contract; Completion owns the terminal diagnostic.
        await Assert.ThrowsAsync<InvalidOperationException>(() => command.ExecuteReaderAsync(Token));
        input.Revoke();
        Assert.Equal(0, input.Reads);
        Assert.Empty(wire.Writer.CommittedBytes);
    }

    [Fact]
    public async Task CancelledIdleReadObservesRetainedFatalBeforeAllowingPacketPublication()
    {
        await using var wire = new IdleWire();
        await using var source = wire.CreateSource();
        using var connection = await source.OpenConnectionAsync(Token);
        await wire.Reader.WaitForIdleReadAsync().WaitAsync(TestTimeout, Token);
        var diagnostic = Diagnostic();
        wire.Reader.BufferedDiagnosticAfterCancelledRead = diagnostic;
        using var input = new BlockingInputMemory();
        using var command = connection.CreateCommand("select $1::bigint[]");
        command.Parameters.Add(new MpgsqlParameter<ReadOnlyMemory<long>>(TypeOid.Int64Array, input.Memory));
        var opening = command.ExecuteReaderAsync(Token);
        var operationError = await Assert.ThrowsAsync<MpgsqlPostgresException>(() => opening.WaitAsync(TestTimeout, Token));
        AssertDiagnostic(Assert.IsType<MpgsqlServerException>(operationError.InnerException));
        var sessionError = await Assert.ThrowsAsync<MpgsqlServerException>(
            () => wire.Session.Completion.WaitAsync(TestTimeout, Token));
        Array.Fill(diagnostic, (byte)'?');
        AssertDiagnostic(sessionError); // Diagnostics own decoded fields beyond the borrowed input.
        Assert.Null(operationError.QueryIndex);
        Assert.Null(operationError.TransactionStatus);
        Assert.Empty(wire.Writer.CommittedBytes);
        Assert.Equal(0, input.Reads);
        input.Revoke();
        Assert.Equal(1, wire.Reader.BufferedTryReadCalls);
        Assert.Equal(1, wire.Reader.BufferedAdvances);
        await source.DisposeAsync().AsTask().WaitAsync(TestTimeout, Token);
        Assert.Equal(1, wire.Reader.CompleteCalls);
        Assert.False(wire.Reader.CompletedDuringPendingRead);
        Assert.Equal(0, wire.Reader.ConcurrentReadFailures);
    }

    [Fact]
    public async Task IdlePartialDiagnosticKeepsItsIdleContextWhenAnOperationTakesOver()
    {
        await using var wire = new IdleWire();
        await using var source = wire.CreateSource();
        using var connection = await source.OpenConnectionAsync(Token);
        await wire.Reader.WaitForReadAfterAsync(0).WaitAsync(TestTimeout, Token);
        var diagnostic = Diagnostic();
        var before = wire.Reader.ReadCalls;
        await wire.WriteAsync(diagnostic.AsMemory(0, 3));
        await wire.Reader.WaitForReadAfterAsync(before).WaitAsync(TestTimeout, Token);
        using var command = connection.CreateCommand("select $1");
        command.Parameters.Add(MpgsqlParameter.Int64(7));
        var opening = command.ExecuteReaderAsync(Token);
        Assert.Equal(Expected(7), await wire.ReadOutputAsync());
        var remainder = wire.PublishDiagnosticAsync(diagnostic[3..]);
        var operationError = await Assert.ThrowsAsync<MpgsqlPostgresException>(() => opening.WaitAsync(TestTimeout, Token));
        AssertDiagnostic(Assert.IsType<MpgsqlServerException>(operationError.InnerException));
        Assert.Null(operationError.QueryIndex);
        Assert.Null(operationError.TransactionStatus);
        AssertDiagnostic(await Assert.ThrowsAsync<MpgsqlServerException>(
            () => wire.Session.Completion.WaitAsync(TestTimeout, Token)));
        await remainder.WaitAsync(TestTimeout, Token);
        Assert.False(wire.Session.IsHealthy);
        Assert.Equal(0, wire.Reader.ConcurrentReadFailures);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task IdleHandoffPreservesRepeatedExecutionsAndAdministration(bool externalFactory)
    {
        await using var wire = new IdleWire(initiallyAdo: !externalFactory);
        await using var source = wire.CreateSource();
        using var connection = await source.OpenConnectionAsync(Token);
        using var command = connection.CreateCommand("select $1");
        command.Parameters.Add(MpgsqlParameter.Int64(7));
        for (var i = 0; i < 3; i++)
        {
            await wire.Reader.WaitForIdleReadAsync().WaitAsync(TestTimeout, Token);
            var opening = command.ExecuteReaderAsync(Token);
            Assert.Equal(Expected(7 + i), await wire.ReadOutputAsync());
            var writing = wire.WriteAsync(Join(Query(7 + i), Ready()));
            using var reader = await opening.WaitAsync(TestTimeout, Token);
            Assert.True(await reader.ReadAsync(Token));
            Assert.Equal(7 + i, reader.GetInt64(0));
            Assert.False(await reader.ReadAsync(Token));
            Assert.False(await reader.NextResultAsync(Token));
            await writing.WaitAsync(TestTimeout, Token);
            Assert.True(wire.Session.IsIdleAndHealthy);
            command.Parameters[0].Value = 8L + i;
        }
        await wire.Reader.WaitForIdleReadAsync().WaitAsync(TestTimeout, Token);
        var preparing = command.PrepareAsync(Token);
        var expected = new ArrayBufferWriter<byte>();
        FrontendMessage.Parse(command.CommandText, "mpgsql_ps_1", new uint[] { 20 }).Write(expected);
        FrontendMessage.Sync().Write(expected);
        Assert.Equal(expected.WrittenSpan.ToArray(), await wire.ReadUntilSyncAsync());
        var preparedReply = wire.WriteAsync(Join(Packet('1'), Ready()));
        await preparing.WaitAsync(TestTimeout, Token);
        await preparedReply.WaitAsync(TestTimeout, Token);
        Assert.True(wire.Session.IsIdleAndHealthy);
        Assert.Equal(0, wire.Reader.ConcurrentReadFailures);
    }

    [Fact]
    public async Task SourceDisposalStopsPendingMonitorBeforeCompletingInputOnce()
    {
        await using var wire = new IdleWire();
        await using var source = wire.CreateSource();
        using var connection = await source.OpenConnectionAsync(Token);
        await wire.Reader.WaitForReadAfterAsync(0).WaitAsync(TestTimeout, Token);
        await source.DisposeAsync().AsTask().WaitAsync(TestTimeout, Token);
        Assert.Equal(1, wire.Reader.CompleteCalls);
        Assert.Equal(0, wire.Reader.ConcurrentReadFailures);
        Assert.False(wire.Reader.CompletedDuringPendingRead);
        await wire.Session.DisposeAsync();
        Assert.Equal(1, wire.Reader.CompleteCalls);
    }

    [Fact]
    public async Task IdleInputFailureRemainsPrimaryAfterLifetimeWake()
    {
        await using var wire = new IdleWire();
        await wire.Reader.WaitForReadAfterAsync(0).WaitAsync(TestTimeout, Token);
        var failure = new IOException("Original idle transport failure.");
        await wire.FailInputAsync(failure);
        var observed = await Assert.ThrowsAsync<IOException>(() => wire.Session.Completion.WaitAsync(TestTimeout, Token));
        Assert.Same(failure, observed);
        Assert.False(wire.Session.IsHealthy);
    }

    [Fact]
    public async Task BuiltinAdoOpeningAutonomouslyReceivesFatalAfterStartup()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var opening = MpgsqlMessageSession.OpenAdoAsync(new MpgsqlSessionOptions
        {
            Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port,
            Username = "idle-test", SslMode = MpgsqlSslMode.Disable
        }, Token).AsTask();
        using var peer = await listener.AcceptTcpClientAsync(Token).AsTask().WaitAsync(TestTimeout, Token);
        var stream = peer.GetStream();
        var length = new byte[4];
        await stream.ReadExactlyAsync(length, Token).AsTask().WaitAsync(TestTimeout, Token);
        var startup = new byte[BinaryPrimitives.ReadInt32BigEndian(length) - 4];
        await stream.ReadExactlyAsync(startup, Token).AsTask().WaitAsync(TestTimeout, Token);
        Assert.Equal(196608, BinaryPrimitives.ReadInt32BigEndian(startup));
        await stream.WriteAsync(Join(Packet('R', [0, 0, 0, 0]),
            Packet('K', [0, 0, 0, 7, 0, 0, 0, 11]), Ready()), Token);
        await using var session = await opening.WaitAsync(TestTimeout, Token);
        // The peer keeps its stream open: autonomous diagnosis cannot depend on EOF.
        await stream.WriteAsync(Diagnostic(), Token);
        AssertDiagnostic(await Assert.ThrowsAsync<MpgsqlServerException>(() => session.Completion.WaitAsync(TestTimeout, Token)));
        Assert.False(session.IsHealthy);
    }

    private static byte[] Diagnostic(string severity = "FATAL") => Packet('E',
        Encoding.UTF8.GetBytes($"Slocalized\0V{severity}\0C57P01\0Moriginal idle reason\0Doriginal detail\0\0"));

    private static void AssertDiagnostic(MpgsqlServerException error, string severity = "FATAL")
    {
        Assert.Equal("57P01", error.SqlState);
        Assert.Equal("original idle reason", error.Message);
        Assert.Equal(severity, error.Diagnostics.InvariantSeverity);
        Assert.Equal("original detail", error.Diagnostics.GetField((byte)'D'));
        Assert.Null(error.QueryIndex);
        Assert.Null(error.TransactionStatus);
    }

    private static byte[] Expected(long value)
    {
        var output = new ArrayBufferWriter<byte>();
        FrontendMessage.Parse("select $1", parameterTypes: new uint[] { 20 }).Write(output);
        FrontendMessage.Bind(parameters: new ReadOnlyMemory<byte>?[] { Int64(value) },
            parameterFormats: new[] { FormatCode.Binary }, resultFormats: new[] { FormatCode.Binary }).Write(output);
        FrontendMessage.Describe(StatementOrPortal.Portal).Write(output);
        FrontendMessage.Execute().Write(output);
        FrontendMessage.Sync().Write(output);
        return output.WrittenSpan.ToArray();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TestTimeout);
        while (!condition())
            await Task.Delay(1, deadline.Token);
    }

    private sealed class IdleWire : IAsyncDisposable
    {
        private readonly Pipe _incoming = new(new PipeOptions(pauseWriterThreshold: 32, resumeWriterThreshold: 16, useSynchronizationContext: false));
        private readonly Pipe _outgoing = new(new PipeOptions(pauseWriterThreshold: 0, useSynchronizationContext: false));
        internal IdleReader Reader { get; }
        internal RecordingWriter Writer { get; }
        internal MpgsqlMessageSession Session { get; }
        internal IdleWire(bool initiallyAdo = true)
        {
            Reader = new IdleReader(_incoming.Reader);
            Writer = new RecordingWriter(_outgoing.Writer);
            if (initiallyAdo)
            {
                var constructor = typeof(MpgsqlMessageSession).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                    .Single(candidate => candidate.GetParameters().Length == 5);
                Session = (MpgsqlMessageSession)constructor.Invoke([Reader, Writer, CancellationToken.None, null, true]);
            }
            else
                Session = new MpgsqlMessageSession(Reader, Writer, Token);
        }
        internal MpgsqlDataSource CreateSource() => new(_ => ValueTask.FromResult(Session), (_, _) => ValueTask.CompletedTask);
        internal async Task WriteAsync(ReadOnlyMemory<byte> bytes, int fragment = int.MaxValue)
        {
            for (var offset = 0; offset < bytes.Length;)
            {
                var size = Math.Min(fragment, bytes.Length - offset);
                await _incoming.Writer.WriteAsync(bytes.Slice(offset, size), Token).AsTask().WaitAsync(TestTimeout, Token);
                offset += size;
            }
        }
        internal async Task PublishDiagnosticAsync(byte[] bytes, int fragment = int.MaxValue)
        {
            try { await WriteAsync(bytes, fragment); }
            catch (MpgsqlServerException) { } // FATAL can complete input before the test flush returns.
        }
        internal ValueTask FailInputAsync(Exception error) => _incoming.Writer.CompleteAsync(error);
        internal async Task<byte[]> ReadOutputAsync()
        {
            var read = await _outgoing.Reader.ReadAsync(Token).AsTask().WaitAsync(TestTimeout, Token);
            var output = read.Buffer.ToArray();
            _outgoing.Reader.AdvanceTo(read.Buffer.End);
            return output;
        }
        internal async Task<byte[]> ReadUntilSyncAsync()
        {
            var output = new ArrayBufferWriter<byte>();
            do { output.Write(await ReadOutputAsync()); }
            while (output.WrittenCount < 5 || !output.WrittenSpan[^5..].SequenceEqual(new byte[] { (byte)'S', 0, 0, 0, 4 }));
            return output.WrittenSpan.ToArray();
        }
        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync().AsTask().WaitAsync(TestTimeout, Token);
            await _incoming.Writer.CompleteAsync();
            await _outgoing.Reader.CompleteAsync();
        }
    }

    private sealed class IdleReader(PipeReader inner) : PipeReader
    {
        private readonly Lock _gate = new();
        private TaskCompletionSource? _changed;
        private int _reads, _outstanding, _pending, _overlaps, _completeCalls;
        private bool _currentReadCanCancel;
        private ReadOnlySequence<byte> _bufferedAfterCancellation;
        private bool _customReadOutstanding;
        internal byte[]? BufferedDiagnosticAfterCancelledRead;
        internal int BufferedTryReadCalls, BufferedAdvances;
        internal bool CompletedDuringPendingRead;
        internal int ReadCalls => Volatile.Read(ref _reads);
        internal int CompleteCalls => Volatile.Read(ref _completeCalls);
        internal int ConcurrentReadFailures => Volatile.Read(ref _overlaps);
        internal Task WaitForReadAfterAsync(int previous)
        {
            lock (_gate)
                return _reads > previous ? Task.CompletedTask
                    : (_changed ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
        internal async Task WaitForIdleReadAsync()
        {
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    if (Volatile.Read(ref _outstanding) != 0 && _currentReadCanCancel)
                        return;
                    changed = (_changed ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
                }
                await changed.ConfigureAwait(false);
            }
        }
        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _outstanding, 1) != 0)
                Interlocked.Increment(ref _overlaps);
            lock (_gate)
            {
                _reads++;
                _currentReadCanCancel = cancellationToken.CanBeCanceled;
                _changed?.TrySetResult();
                _changed = null;
            }
            Interlocked.Increment(ref _pending);
            return ObserveAsync(cancellationToken);
        }
        private async ValueTask<ReadResult> ObserveAsync(CancellationToken token)
        {
            try { return await inner.ReadAsync(token).ConfigureAwait(false); }
            catch
            {
                Volatile.Write(ref _outstanding, 0);
                if (token.IsCancellationRequested && BufferedDiagnosticAfterCancelledRead is { } buffered)
                {
                    BufferedDiagnosticAfterCancelledRead = null;
                    _bufferedAfterCancellation = new ReadOnlySequence<byte>(buffered);
                }
                throw;
            }
            finally { Interlocked.Decrement(ref _pending); }
        }
        private bool AdvancedCustomBuffer(SequencePosition consumed)
        {
            if (!_customReadOutstanding)
                return false;
            Assert.Equal(_bufferedAfterCancellation.End, consumed);
            _customReadOutstanding = false;
            _bufferedAfterCancellation = default;
            BufferedAdvances++;
            Volatile.Write(ref _outstanding, 0);
            return true;
        }
        public override void AdvanceTo(SequencePosition consumed)
        {
            if (AdvancedCustomBuffer(consumed))
                return;
            inner.AdvanceTo(consumed);
            Volatile.Write(ref _outstanding, 0);
        }
        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
        {
            if (AdvancedCustomBuffer(consumed))
                return;
            inner.AdvanceTo(consumed, examined);
            Volatile.Write(ref _outstanding, 0);
        }
        public override bool TryRead(out ReadResult result)
        {
            if (!_bufferedAfterCancellation.IsEmpty)
            {
                if (Interlocked.Exchange(ref _outstanding, 1) != 0)
                    Interlocked.Increment(ref _overlaps);
                _customReadOutstanding = true;
                BufferedTryReadCalls++;
                result = new ReadResult(_bufferedAfterCancellation, false, false);
                return true;
            }
            var available = inner.TryRead(out result);
            if (available && Interlocked.Exchange(ref _outstanding, 1) != 0)
                Interlocked.Increment(ref _overlaps);
            return available;
        }
        public override void CancelPendingRead() => inner.CancelPendingRead();
        private void Completing()
        {
            Interlocked.Increment(ref _completeCalls);
            CompletedDuringPendingRead |= Volatile.Read(ref _pending) != 0;
        }
        public override void Complete(Exception? exception = null) { Completing(); inner.Complete(exception); }
        public override ValueTask CompleteAsync(Exception? exception = null) { Completing(); return inner.CompleteAsync(exception); }
    }

    private sealed class RecordingWriter(PipeWriter inner) : PipeWriter
    {
        private readonly ArrayBufferWriter<byte> _committed = new();
        private Memory<byte> _memory;
        internal byte[] CommittedBytes => _committed.WrittenSpan.ToArray();
        public override Memory<byte> GetMemory(int sizeHint = 0) => _memory = inner.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
        public override void Advance(int bytes) { _committed.Write(_memory.Span[..bytes]); _memory = default; inner.Advance(bytes); }
        public override void CancelPendingFlush() => inner.CancelPendingFlush();
        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) => inner.FlushAsync(cancellationToken);
        public override void Complete(Exception? exception = null) => inner.Complete(exception);
        public override ValueTask CompleteAsync(Exception? exception = null) => inner.CompleteAsync(exception);
    }
}
