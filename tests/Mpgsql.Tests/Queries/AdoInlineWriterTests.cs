using System.Buffers;
using System.IO.Pipelines;
using System.Reflection;
using Mpgsql.Internal;
using Mpgsql.Protocol;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class AdoInlineWriterTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly AsyncLocal<object?> CallerContext = new();

    [Theory, InlineData(false), InlineData(true)]
    public async Task SmallSingleExecutionStartsOnCallerAndWritesExactUnnamedOrPreparedPacket(bool prepared)
    {
        await using var wire = new WriterWire();
        await using var source = wire.CreateSource();
        using var connection = await source.OpenConnectionAsync(Token);
        using var command = connection.CreateCommand("select $1");
        command.Parameters.Add(MpgsqlParameter.Int64(7));
        if (prepared)
        {
            var preparing = command.PrepareAsync(Token);
            await wire.SyncAsync();
            await wire.WriteAsync(Join(Packet('1'), Ready()));
            await preparing.WaitAsync(TestTimeout, Token);
            // Delivery can signal before the writer finishes its cleanup tail. The
            // inline-path assertion requires the same output-idle boundary as handoff.
            var idle = (Task)typeof(MpgsqlMessageSession)
                .GetMethod("WaitForBackgroundOutputIdleAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(wire.Session, null)!;
            await idle.WaitAsync(TestTimeout, Token);
        }
        wire.Writer.ResetObservation();
        var marker = new object();
        CallerContext.Value = marker;
        try
        {
            var opening = command.ExecuteReaderAsync(Token);
            var expected = Expected(command.CommandText, prepared ? "mpgsql_ps_1" : null, 7);
            Assert.Equal(expected, await wire.SyncAsync());
            Assert.Same(marker, wire.Writer.FirstContext);
            await FinishValueAsync(wire, opening, prepared);
        }
        finally { CallerContext.Value = null; }
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Theory, InlineData(65530, true), InlineData(65531, false)]
    public async Task CompletePacketAndSyncBoundSelectsInlineOrExistingWriterWithoutChangingBytes(int packetSize, bool inline)
    {
        await using var wire = new WriterWire();
        await using var source = wire.CreateSource();
        using var connection = await source.OpenConnectionAsync(Token);
        var emptySize = new QueryDefinition("", default).Measure();
        using var command = connection.CreateCommand(new string('x', packetSize - emptySize));
        Assert.Equal(packetSize, new QueryDefinition(command.CommandText, default).Measure());
        var marker = new object();
        CallerContext.Value = marker;
        try
        {
            var opening = command.ExecuteReaderAsync(Token);
            Assert.Equal(Expected(command.CommandText), await wire.SyncAsync());
            if (inline)
                Assert.Same(marker, wire.Writer.FirstContext);
            else
                Assert.Null(wire.Writer.FirstContext);
            await FinishValueAsync(wire, opening);
        }
        finally { CallerContext.Value = null; }
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Fact]
    public async Task MixedPreparedSixteenCommandBatchStartsOnCallerAndPreservesFullWireAndResults()
    {
        await using var wire = new WriterWire();
        await using var source = wire.CreateSource();
        using var connection = await source.OpenConnectionAsync(Token);
        using var batch = connection.CreateBatch();
        for (var i = 0; i < 8; i++)
        {
            var command = new MpgsqlBatchCommand("select $1");
            command.Parameters.Add(MpgsqlParameter.Int64(7 + i * 2));
            batch.BatchCommands.Add(command);
        }
        var preparing = batch.PrepareAsync(Token);
        var preparation = new ArrayBufferWriter<byte>();
        for (var i = 0; i < 8; i++)
            FrontendMessage.Parse("select $1", $"mpgsql_ps_{i + 1}", new uint[] { 20 }).Write(preparation);
        FrontendMessage.Sync().Write(preparation);
        Assert.Equal(preparation.WrittenSpan.ToArray(), await wire.SyncAsync());
        await Task.WhenAll(preparing, wire.WriteAsync(Join([.. Enumerable.Repeat(Packet('1'), 8), Ready()])))
            .WaitAsync(TestTimeout, Token);
        await WaitForBackgroundOutputIdleAsync(wire);
        // Adding commands after Prepare retains the existing named statements and
        // gives one legitimate mixed array of prepared and unnamed executions.
        for (var i = 0; i < 8; i++)
        {
            var command = new MpgsqlBatchCommand("select $1");
            command.Parameters.Add(MpgsqlParameter.Int64(8 + i * 2));
            batch.BatchCommands.Insert(i * 2 + 1, command);
        }
        wire.Writer.ResetObservation();
        var marker = new object();
        CallerContext.Value = marker;
        try
        {
            var opening = batch.ExecuteReaderAsync(Token);
            Assert.Equal(ExpectedBatch(batch), await wire.SyncAsync());
            Assert.Same(marker, wire.Writer.FirstContext);
            var responses = new List<byte[]>();
            for (var i = 0; i < 16; i++)
                responses.Add(Join(i % 2 == 0 ? Join(Packet('2'), Description(20)) : Begin(20), Row(Int64(7 + i)), Command()));
            responses.Add(Ready());
            var writing = wire.WriteAsync(Join([.. responses]));
            using var reader = await opening.WaitAsync(TestTimeout, Token);
            for (var i = 0; i < 16; i++)
            {
                Assert.True(await reader.ReadAsync(Token));
                Assert.Equal(7L + i, reader.GetInt64(0));
                Assert.False(await reader.ReadAsync(Token));
                Assert.Equal(i != 15, await reader.NextResultAsync(Token));
            }
            await writing.WaitAsync(TestTimeout, Token);
        }
        finally { CallerContext.Value = null; }
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Theory, InlineData(255, true), InlineData(256, false)]
    public async Task CommandCountBoundKeepsWriterChunkFallbackAndExactBatchBytes(int count, bool inline)
    {
        await using var wire = new WriterWire();
        await using var source = wire.CreateSource();
        using var connection = await source.OpenConnectionAsync(Token);
        using var batch = connection.CreateBatch();
        for (var i = 0; i < count; i++)
            batch.BatchCommands.Add(new MpgsqlBatchCommand("select empty"));
        wire.Writer.ResetObservation();
        var marker = new object();
        CallerContext.Value = marker;
        try
        {
            var opening = batch.ExecuteReaderAsync(Token);
            Assert.Equal(ExpectedBatch(batch), await wire.SyncAsync());
            if (inline)
                Assert.Same(marker, wire.Writer.FirstContext);
            else
                Assert.Null(wire.Writer.FirstContext);
            await FinishEmptyBatchAsync(wire, opening, count);
        }
        finally { CallerContext.Value = null; }
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Theory, InlineData(65535, true), InlineData(65536, false)]
    public async Task SixteenCommandTotalByteBoundIncludesOneSyncAndKeepsExactBatchBytes(int totalBytes, bool inline)
    {
        await using var wire = new WriterWire();
        await using var source = wire.CreateSource();
        using var connection = await source.OpenConnectionAsync(Token);
        using var batch = connection.CreateBatch();
        var emptySize = new QueryDefinition("", default).Measure();
        for (var i = 0; i < 15; i++)
            batch.BatchCommands.Add(new MpgsqlBatchCommand(""));
        batch.BatchCommands.Add(new MpgsqlBatchCommand(new string('x', totalBytes - 5 - 16 * emptySize)));
        var expected = ExpectedBatch(batch);
        Assert.Equal(totalBytes, expected.Length);
        var marker = new object();
        CallerContext.Value = marker;
        try
        {
            var opening = batch.ExecuteReaderAsync(Token);
            Assert.Equal(expected, await wire.SyncAsync());
            if (inline)
                Assert.Same(marker, wire.Writer.FirstContext);
            else
                Assert.Null(wire.Writer.FirstContext);
            await FinishEmptyBatchAsync(wire, opening, 16);
        }
        finally { CallerContext.Value = null; }
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Fact]
    public async Task SixteenCommandBatchValidatesLastCommandBeforeAnyInlinePublication()
    {
        await using var wire = new WriterWire();
        await using var source = wire.CreateSource();
        using var connection = await source.OpenConnectionAsync(Token);
        using var batch = connection.CreateBatch();
        for (var i = 0; i < 15; i++)
            batch.BatchCommands.Add(new MpgsqlBatchCommand("select valid"));
        var invalid = new MpgsqlBatchCommand("select $1");
        invalid.Parameters.Add(new MpgsqlParameter());
        batch.BatchCommands.Add(invalid);
        await Assert.ThrowsAsync<InvalidOperationException>(() => batch.ExecuteReaderAsync(Token));
        Assert.Equal(0, wire.Writer.SpanCalls);
        invalid.Parameters.Clear();
        invalid.CommandText = "select recovered";
        var opening = batch.ExecuteReaderAsync(Token);
        Assert.Equal(ExpectedBatch(batch), await wire.SyncAsync());
        await FinishEmptyBatchAsync(wire, opening, 16);
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Fact]
    public async Task ManualCancellationWithoutRequestTokenKeepsPublishedBatchPrefixAndSkipsBorrowedRemainder()
    {
        await using var wire = new WriterWire();
        var cancelSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var source = wire.CreateSource(() => cancelSent.TrySetResult());
        using var connection = await source.OpenConnectionAsync(Token);
        using var batch = connection.CreateBatch();
        batch.BatchCommands.Add(new MpgsqlBatchCommand("select first"));
        using var input = new BlockingInputMemory();
        var borrowed = new MpgsqlBatchCommand("select $1::bigint[]");
        borrowed.Parameters.Add(new MpgsqlParameter<ReadOnlyMemory<long>>(TypeOid.Int64Array, input.Memory));
        batch.BatchCommands.Add(borrowed);
        for (var i = 0; i < 14; i++)
            batch.BatchCommands.Add(new MpgsqlBatchCommand("select skipped"));
        var advances = 0;
        wire.Writer.AfterAdvance = () =>
        {
            if (++advances == 1)
                batch.Cancel();
        };
        // The first packet is registered before Advance; its manual cancellation
        // must preserve that published prefix and the sole recovery Sync.
#pragma warning disable xUnit1051
        var opening = batch.ExecuteReaderAsync();
#pragma warning restore xUnit1051
        Assert.Equal(Expected("select first"), await wire.SyncAsync());
        await cancelSent.Task.WaitAsync(TestTimeout, Token);
        Assert.Equal(0, input.Reads);
        input.Revoke();
        await wire.WriteAsync(Join(Error("57014"), Ready()));
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TestTimeout, Token));
        Assert.False(error.CancellationToken.CanBeCanceled);
        Assert.Equal(0, input.Reads);
        borrowed.Parameters.Clear();
        borrowed.CommandText = "reusable after prefix cancellation";
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Fact]
    public async Task ReentrantManualCommandCancellationWithoutRequestTokenSeesItsAttachedExecution()
    {
        await using var wire = new WriterWire();
        var cancelSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var source = wire.CreateSource(() => cancelSent.TrySetResult());
        using var connection = await source.OpenConnectionAsync(Token);
        using var command = connection.CreateCommand("select published command");
        var advances = 0;
        wire.Writer.AfterAdvance = () =>
        {
            if (++advances == 1)
                command.Cancel();
        };
#pragma warning disable xUnit1051
        var opening = command.ExecuteReaderAsync();
#pragma warning restore xUnit1051
        Assert.Equal(Expected(command.CommandText), await wire.SyncAsync());
        await cancelSent.Task.WaitAsync(TestTimeout, Token);
        await wire.WriteAsync(Join(Error("57014"), Ready()));
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TestTimeout, Token));
        Assert.False(error.CancellationToken.CanBeCanceled);
        command.CommandText = "reusable after synchronous publication cancellation";
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Fact]
    public async Task CancellationBeforePublicationWritesOnlySyncAndDoesNotSendCancelRequest()
    {
        await using var wire = new WriterWire();
        var cancels = 0;
        await using var source = wire.CreateSource(() => Interlocked.Increment(ref cancels));
        using var connection = await source.OpenConnectionAsync(Token);
        using var command = connection.CreateCommand("select $1");
        command.Parameters.Add(MpgsqlParameter.Int64(7));
        using var request = new CancellationTokenSource();
        wire.Writer.BeforeFirstSpan = request.Cancel;
        var opening = command.ExecuteReaderAsync(request.Token);
        Assert.Equal(SyncBytes(), await wire.SyncAsync());
        await wire.WriteAsync(Ready());
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TestTimeout, Token));
        Assert.Equal(request.Token, error.CancellationToken);
        Assert.Equal(0, cancels);
        Assert.True(wire.Session.IsIdleAndHealthy);
        command.CommandText = "reusable after unpublished cancellation";
    }

    [Fact]
    public async Task CancellationDuringInlineEncodingKeepsInputUntilEncodingStopsAndThenRecovers()
    {
        await using var wire = new WriterWire();
        await using var source = wire.CreateSource();
        using var connection = await source.OpenConnectionAsync(Token);
        using var command = connection.CreateCommand("select $1::bigint[]");
        using var input = new BlockingInputMemory(true);
        command.Parameters.Add(new MpgsqlParameter<ReadOnlyMemory<long>>(TypeOid.Int64Array, input.Memory));
        using var request = new CancellationTokenSource();
        // Inline encoding deliberately owns the caller until its first suspension.
        var opening = Task.Run(() => command.ExecuteReaderAsync(request.Token), Token);
        try
        {
            await input.Entered.WaitAsync(TestTimeout, Token);
            request.Cancel();
            Assert.False(opening.IsCompleted);
            Assert.Equal(1, input.Reads);
        }
        finally { input.Resume(); }
        Assert.Equal(SyncBytes(), await wire.SyncAsync());
        await wire.WriteAsync(Ready());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TestTimeout, Token));
        input.Revoke();
        command.Parameters.Clear();
        command.CommandText = "reusable after encoder cancellation";
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task CancellationDrainsReadyWhileInlineFlushIsStillBlocked(bool batchExecution)
    {
        await using var wire = new WriterWire(blockWrites: true);
        var cancelSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var source = wire.CreateSource(() => cancelSent.TrySetResult());
        using var connection = await source.OpenConnectionAsync(Token);
        using var command = connection.CreateCommand("select held output");
        using var batch = connection.CreateBatch();
        if (batchExecution)
            for (var i = 0; i < 16; i++)
                batch.BatchCommands.Add(new MpgsqlBatchCommand("select held output"));
        var opening = batchExecution ? batch.ExecuteReaderAsync(Token) : command.ExecuteReaderAsync(Token);
        Assert.Equal(batchExecution ? ExpectedBatch(batch) : Expected(command.CommandText), await wire.HoldOutputAsync());
        if (batchExecution)
            batch.Cancel();
        else
            command.Cancel();
        await cancelSent.Task.WaitAsync(TestTimeout, Token);
        // Incoming exceeds its pause threshold. Completion proves that recovery has
        // consumed and advanced ReadyForQuery before output delivery is allowed.
        await wire.WriteAsync(Join(Error("57014"), Ready())).WaitAsync(TestTimeout, Token);
        Assert.False(opening.IsCompleted);
        wire.ReleaseOutput();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TestTimeout, Token));
        Assert.True(wire.Session.IsIdleAndHealthy);
        if (batchExecution)
            batch.BatchCommands[0].CommandText = "reusable after pending delivery cancellation";
        else
            command.CommandText = "reusable after pending delivery cancellation";
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task ManualCancelOrTimeoutWithoutRequestTokenKeepsDeliveryDistinctAndSessionReusable(bool timeout)
    {
        await using var wire = new WriterWire(blockWrites: true);
        var cancelSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var source = wire.CreateSource(() => cancelSent.TrySetResult());
        using var connection = await source.OpenConnectionAsync(Token);
        using var command = connection.CreateCommand("select held default token");
        if (timeout)
            command.CommandTimeout = 1;
        // This case specifically covers cancellation with no request token.
#pragma warning disable xUnit1051
        var opening = command.ExecuteReaderAsync();
#pragma warning restore xUnit1051
        Assert.Equal(Expected(command.CommandText), await wire.HoldOutputAsync());
        if (!timeout)
            command.Cancel();
        await cancelSent.Task.WaitAsync(TestTimeout, Token);
        await wire.WriteAsync(Join(Error("57014"), Ready())).WaitAsync(TestTimeout, Token);
        Assert.False(opening.IsCompleted);
        wire.ReleaseOutput();
        if (timeout)
        {
            var error = await Assert.ThrowsAsync<MpgsqlException>(() => opening.WaitAsync(TestTimeout, Token));
            Assert.IsType<TimeoutException>(error.InnerException);
        }
        else
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TestTimeout, Token));
            Assert.False(error.CancellationToken.CanBeCanceled);
        }
        Assert.True(wire.Session.IsIdleAndHealthy);
        command.CommandTimeout = 0;
        command.CommandText = "reusable after default-token cancellation";
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task SourceDisposalWaitsForInlineFlushBeforeCompletingOutput(bool batchExecution)
    {
        await using var wire = new WriterWire(blockWrites: true);
        await using var source = wire.CreateSource();
        using var connection = await source.OpenConnectionAsync(Token);
        using var command = connection.CreateCommand("select disposed output");
        using var batch = connection.CreateBatch();
        if (batchExecution)
            for (var i = 0; i < 16; i++)
                batch.BatchCommands.Add(new MpgsqlBatchCommand("select disposed output"));
        wire.Writer.HoldCancelledFlush();
        var opening = batchExecution ? batch.ExecuteReaderAsync(Token) : command.ExecuteReaderAsync(Token);
        Assert.Equal(batchExecution ? ExpectedBatch(batch) : Expected(command.CommandText), await wire.HoldOutputAsync());
        var disposing = source.DisposeAsync().AsTask();
        try
        {
            await wire.Writer.CancellationHeld.WaitAsync(TestTimeout, Token);
            Assert.False(disposing.IsCompleted);
            Assert.Equal(0, wire.Writer.CompleteCalls);
        }
        finally { wire.Writer.ReleaseCancelledFlush(); }
        await disposing.WaitAsync(TestTimeout, Token);
        Assert.NotNull(await Record.ExceptionAsync(() => opening.WaitAsync(TestTimeout, Token)));
        Assert.True(opening.IsCompleted);
        Assert.False(wire.Session.IsHealthy);
        Assert.Equal(1, wire.Writer.CompleteCalls);
        Assert.False(wire.Writer.CompletedDuringFlush);
        await source.DisposeAsync();
        await wire.Session.DisposeAsync();
        Assert.Equal(1, wire.Writer.CompleteCalls);
    }

    [Fact]
    public async Task ExternalFactoryWaitsForGeneralWriterDeliveryTailBeforeEnablingInlineOutput()
    {
        await using var wire = new WriterWire(blockWrites: true);
        await using var first = wire.Session.CreateBatch(Token);
        var work = first.SendExecution(new QueryDefinition("select previous owner", default), null);
        Assert.Equal(Expected("select previous owner"), await wire.HoldOutputAsync());
        var discarding = first.DisposeAsync().AsTask();
        await wire.WriteAsync(Join(Begin(20), Command("SELECT 0"), Ready()));
        await discarding.WaitAsync(TestTimeout, Token);
        Assert.False(work.Delivery.IsCompleted);

        // The old batch has received RFQ and relinquished registration, but its
        // physical writer still owns the blocked Flush. Handoff must wait for it.
        await using var source = wire.CreateSource();
        var connecting = source.OpenConnectionAsync(Token).AsTask();
        Assert.False(connecting.IsCompleted);
        wire.ReleaseOutput();
        using var connection = await connecting.WaitAsync(TestTimeout, Token);
        await work.Delivery.WaitAsync(TestTimeout, Token);
        Assert.True(wire.Session.IsAdoSession);
        wire.Writer.ResetObservation();
        using var command = connection.CreateCommand("select next owner");
        var marker = new object();
        CallerContext.Value = marker;
        try
        {
            var opening = command.ExecuteReaderAsync(Token);
            Assert.Equal(Expected(command.CommandText), await wire.SyncAsync());
            Assert.Same(marker, wire.Writer.FirstContext);
            await FinishValueAsync(wire, opening);
        }
        finally { CallerContext.Value = null; }
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Fact]
    public async Task SourceDisposalCancelsHandoffWaitingForUnregisteredGeneralWriterTail()
    {
        await using var wire = new WriterWire(blockWrites: true);
        await using var first = wire.Session.CreateBatch(Token);
        var work = first.SendExecution(new QueryDefinition("select previous owner", default), null);
        Assert.Equal(Expected("select previous owner"), await wire.HoldOutputAsync());
        var discarding = first.DisposeAsync().AsTask();
        await wire.WriteAsync(Join(Begin(20), Command("SELECT 0"), Ready()));
        await discarding.WaitAsync(TestTimeout, Token);
        Assert.False(work.Delivery.IsCompleted);

        await using var source = wire.CreateSource();
        var connecting = source.OpenConnectionAsync(Token).AsTask();
        Assert.False(connecting.IsCompleted);
        // The factory's claimed session is not in DataSource._all until handoff
        // completes. Cancellation must reach that pending claim, not just _all.
        await source.DisposeAsync().AsTask().WaitAsync(TestTimeout, Token);
        Assert.NotNull(await Record.ExceptionAsync(() => connecting.WaitAsync(TestTimeout, Token)));
        Assert.NotNull(await Record.ExceptionAsync(() => work.Delivery.WaitAsync(TestTimeout, Token)));
        Assert.True(connecting.IsCompleted);
        Assert.True(work.Delivery.IsCompleted);
        Assert.False(wire.Session.IsHealthy);
        Assert.Equal(1, wire.Writer.CompleteCalls);
        Assert.False(wire.Writer.CompletedDuringFlush);
    }

    private static async Task FinishValueAsync(WriterWire wire, Task<MpgsqlDataReader> opening, bool prepared = false)
    {
        var writing = wire.WriteAsync(Join(prepared ? Join(Packet('2'), Description(20)) : Begin(20), Row(Int64(7)), Command(), Ready()));
        using var reader = await opening.WaitAsync(TestTimeout, Token);
        Assert.True(await reader.ReadAsync(Token));
        Assert.Equal(7L, reader.GetInt64(0));
        Assert.False(await reader.ReadAsync(Token));
        Assert.False(await reader.NextResultAsync(Token));
        await writing.WaitAsync(TestTimeout, Token);
    }

    private static async Task FinishEmptyBatchAsync(WriterWire wire, Task<MpgsqlDataReader> opening, int count)
    {
        var writing = wire.WriteAsync(Join([.. Enumerable.Repeat(Join(Begin(20), Command("SELECT 0")), count), Ready()]));
        using var reader = await opening.WaitAsync(TestTimeout, Token);
        for (var i = 0; i < count; i++)
        {
            Assert.False(await reader.ReadAsync(Token));
            Assert.Equal(i + 1 != count, await reader.NextResultAsync(Token));
        }
        await writing.WaitAsync(TestTimeout, Token);
    }

    private static async Task WaitForBackgroundOutputIdleAsync(WriterWire wire)
    {
        var idle = (Task)typeof(MpgsqlMessageSession)
            .GetMethod("WaitForBackgroundOutputIdleAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(wire.Session, null)!;
        await idle.WaitAsync(TestTimeout, Token);
    }

    private static byte[] ExpectedBatch(MpgsqlBatch batch)
    {
        var output = new ArrayBufferWriter<byte>();
        foreach (var command in batch.BatchCommands)
        {
            var typed = (MpgsqlBatchCommand)command;
            var packet = Expected(typed.CommandText, typed.Statement?.Name,
                typed.Parameters.Count == 0 ? null : (long)typed.Parameters[0].Value!);
            output.Write(packet.AsSpan(0, packet.Length - 5));
        }
        FrontendMessage.Sync().Write(output);
        return output.WrittenSpan.ToArray();
    }

    private static byte[] Expected(string sql, string? statement = null, long? value = null)
    {
        var output = new ArrayBufferWriter<byte>();
        if (statement is null)
            FrontendMessage.Parse(sql, parameterTypes: value.HasValue ? new uint[] { 20 } : ReadOnlyMemory<uint>.Empty).Write(output);
        FrontendMessage.Bind(statement: statement ?? "", parameters: value.HasValue ? new ReadOnlyMemory<byte>?[] { Int64(value.Value) } : ReadOnlyMemory<ReadOnlyMemory<byte>?>.Empty,
            parameterFormats: new[] { FormatCode.Binary }, resultFormats: new[] { FormatCode.Binary }).Write(output);
        FrontendMessage.Describe(StatementOrPortal.Portal).Write(output);
        FrontendMessage.Execute().Write(output);
        FrontendMessage.Sync().Write(output);
        return output.WrittenSpan.ToArray();
    }

    private static byte[] SyncBytes()
    {
        var output = new ArrayBufferWriter<byte>();
        FrontendMessage.Sync().Write(output);
        return output.WrittenSpan.ToArray();
    }

    private sealed class WriterWire : IAsyncDisposable
    {
        private readonly Pipe _incoming = new(new PipeOptions(pauseWriterThreshold: 32, resumeWriterThreshold: 16, useSynchronizationContext: false));
        private readonly Pipe _outgoing;
        private ReadResult _held;
        private bool _hasHeld;
        internal TrackingWriter Writer { get; }
        internal MpgsqlMessageSession Session { get; }
        internal WriterWire(bool blockWrites = false)
        {
            _outgoing = new Pipe(new PipeOptions(pauseWriterThreshold: blockWrites ? 1 : 0, resumeWriterThreshold: blockWrites ? 1 : 0, useSynchronizationContext: false));
            Writer = new TrackingWriter(_outgoing.Writer);
            Session = new MpgsqlMessageSession(_incoming.Reader, Writer);
        }
        internal MpgsqlDataSource CreateSource(Action? cancel = null) => new(_ => ValueTask.FromResult(Session), (_, _) =>
        {
            cancel?.Invoke();
            return ValueTask.CompletedTask;
        });
        internal async Task WriteAsync(ReadOnlyMemory<byte> bytes)
        {
            await _incoming.Writer.WriteAsync(bytes, Token).AsTask().WaitAsync(TestTimeout, Token);
        }
        internal async Task<byte[]> HoldOutputAsync()
        {
            _held = await _outgoing.Reader.ReadAsync(Token).AsTask().WaitAsync(TestTimeout, Token);
            _hasHeld = true;
            return _held.Buffer.ToArray();
        }
        internal void ReleaseOutput()
        {
            if (!_hasHeld)
                return;
            _outgoing.Reader.AdvanceTo(_held.Buffer.End);
            _hasHeld = false;
        }
        internal async Task<byte[]> SyncAsync()
        {
            var bytes = new List<byte>();
            do
            {
                bytes.AddRange(await HoldOutputAsync());
                ReleaseOutput();
            }
            while (!Tags([.. bytes]).Contains('S'));
            return [.. bytes];
        }
        public async ValueTask DisposeAsync()
        {
            Writer.ReleaseCancelledFlush();
            ReleaseOutput();
            await Session.DisposeAsync().AsTask().WaitAsync(TestTimeout, Token);
            await _incoming.Writer.CompleteAsync();
            await _outgoing.Reader.CompleteAsync();
        }
    }

    private sealed class TrackingWriter(PipeWriter inner) : PipeWriter
    {
        private TaskCompletionSource? _release;
        private readonly TaskCompletionSource _cancellationHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _spans, _activeFlushes, _completeCalls, _completedDuringFlush;
        internal Action? BeforeFirstSpan;
        internal Action? AfterAdvance;
        internal object? FirstContext { get; private set; }
        internal Task CancellationHeld => _cancellationHeld.Task;
        internal int CompleteCalls => Volatile.Read(ref _completeCalls);
        internal int SpanCalls => Volatile.Read(ref _spans);
        internal bool CompletedDuringFlush => Volatile.Read(ref _completedDuringFlush) != 0;
        internal void ResetObservation() { _spans = 0; FirstContext = null; }
        internal void HoldCancelledFlush() => _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void ReleaseCancelledFlush() => _release?.TrySetResult();
        private void ObserveSpan()
        {
            if (Interlocked.Increment(ref _spans) != 1)
                return;
            FirstContext = CallerContext.Value;
            BeforeFirstSpan?.Invoke();
        }
        public override Memory<byte> GetMemory(int sizeHint = 0) { ObserveSpan(); return inner.GetMemory(sizeHint); }
        public override Span<byte> GetSpan(int sizeHint = 0) { ObserveSpan(); return inner.GetSpan(sizeHint); }
        public override void Advance(int bytes) { inner.Advance(bytes); AfterAdvance?.Invoke(); }
        public override void CancelPendingFlush() => inner.CancelPendingFlush();
        public override async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _activeFlushes);
            try
            {
                var result = await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return result;
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested && _release is not null)
            {
                _cancellationHeld.TrySetResult();
                await _release.Task.ConfigureAwait(false);
                throw;
            }
            finally { Interlocked.Decrement(ref _activeFlushes); }
        }
        public override void Complete(Exception? exception = null)
        {
            ObserveComplete();
            inner.Complete(exception);
        }
        public override ValueTask CompleteAsync(Exception? exception = null)
        {
            ObserveComplete();
            return inner.CompleteAsync(exception);
        }
        private void ObserveComplete()
        {
            Interlocked.Increment(ref _completeCalls);
            if (Volatile.Read(ref _activeFlushes) != 0)
                Volatile.Write(ref _completedDuringFlush, 1);
        }
    }
}
