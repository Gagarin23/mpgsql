using System.Buffers;
using System.IO.Pipelines;
using Mpgsql.Protocol;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class AdoFusedInitializationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static byte[] MetadataSeen => Packet('S', "fused_metadata\0seen\0"u8.ToArray());

    [Fact]
    public async Task SourceDisposalWhileFirstRowIsPendingWaitsForInitializedReaderOwnership()
    {
        await using var wire = new FusedWire();
        await using var source = wire.CreateSource();
        using var connection = await source.OpenConnectionAsync(Token);
        using var command = connection.CreateCommand("select $1");
        command.Parameters.Add(MpgsqlParameter.Int64(7));
        var opening = command.ExecuteReaderAsync(Token);
        Assert.Equal(Expected("select $1", 7), await wire.ReadOutputAsync());
        await wire.WriteAsync(Join(Begin(20), MetadataSeen));
        await WaitForMetadataAsync(wire.Session);
        Assert.False(opening.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => command.Parameters[0].Value = 9L);
        await source.DisposeAsync().AsTask().WaitAsync(TestTimeout, Token);
        var error = await Record.ExceptionAsync(() => opening.WaitAsync(TestTimeout, Token));
        Assert.NotNull(error);
        Assert.IsNotType<TimeoutException>(error);
        Assert.Equal(1, wire.Reader.CompleteCalls);
        Assert.False(wire.Reader.CompletedWithPendingRead);
        Assert.False(wire.Session.IsHealthy);
        command.Parameters[0].Value = 9L;
    }

    [Theory, InlineData(1), InlineData(int.MaxValue)]
    public async Task FirstRowPrefetchRoutesInterleavedMessagesAndRemainsUnpositionedForPublicGetters(int fragment)
    {
        await using var wire = new FusedWire();
        await using var source = wire.CreateSource();
        using var connection = await source.OpenConnectionAsync(Token);
        using var command = connection.CreateCommand("select $1");
        command.Parameters.Add(MpgsqlParameter.Int64(7));
        var opening = command.ExecuteReaderAsync(Token);
        Assert.Equal(Expected("select $1", 7), await wire.ReadOutputAsync());
        var writing = wire.WriteAsync(Join(Begin(20), MetadataSeen,
            Packet('N', "SNOTICE\0Mfirst row notice\0\0"u8.ToArray()),
            Packet('A', [0, 0, 0, 7, .. "events\0first row\0"u8.ToArray()]),
            Row(Int64(7)), Command(), Ready()), fragment);
        using var reader = await opening.WaitAsync(TestTimeout, Token);
        Assert.True(reader.HasRows);
        Assert.Equal(1, reader.FieldCount);
        Assert.Throws<InvalidOperationException>(() => reader.GetInt64(0));
        Assert.Throws<InvalidOperationException>(() => reader.GetRawValue(0));
        Assert.True(await reader.ReadAsync(Token));
        Assert.Equal(7L, reader.GetInt64(0));
        Assert.Equal(Int64(7), reader.GetRawValue(0)!.Value.ToArray());
        Assert.False(await reader.ReadAsync(Token));
        Assert.Throws<InvalidOperationException>(() => reader.GetRawValue(0));
        Assert.False(await reader.NextResultAsync(Token));
        await writing.WaitAsync(TestTimeout, Token);
        Assert.True(wire.Session.TryReadNotice(out var notice));
        Assert.Equal("first row notice", notice.Message);
        Assert.True(wire.Session.TryReadNotification(out var notification));
        Assert.Equal("first row", notification.Payload);
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task EmptyAndSqlNullFirstResultsKeepHasRowsAndValueSemantics(bool sqlNull)
    {
        await using var wire = new FusedWire();
        await using var source = wire.CreateSource();
        using var connection = await source.OpenConnectionAsync(Token);
        using var command = connection.CreateCommand("select $1");
        command.Parameters.Add(MpgsqlParameter.Int64(7));
        var opening = command.ExecuteReaderAsync(Token);
        Assert.Equal(Expected("select $1", 7), await wire.ReadOutputAsync());
        var writing = wire.WriteAsync(sqlNull
            ? Join(Begin(20), Row((byte[]?)null), Command(), Ready())
            : Join(Begin(20), Command("SELECT 0"), Ready()));
        using var reader = await opening.WaitAsync(TestTimeout, Token);
        Assert.Equal(sqlNull, reader.HasRows);
        Assert.Equal(sqlNull, await reader.ReadAsync(Token));
        if (sqlNull)
        {
            Assert.True(reader.IsDBNull(0));
            Assert.Same(DBNull.Value, reader.GetValue(0));
            Assert.Throws<InvalidCastException>(() => reader.GetFieldValue<long>(0));
            Assert.False(await reader.ReadAsync(Token));
        }
        Assert.False(await reader.NextResultAsync(Token));
        await writing.WaitAsync(TestTimeout, Token);
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Fact]
    public async Task ErrorAfterMetadataIsRecoveredBeforeInitializationFaultAndCommandReuse()
    {
        await using var wire = new FusedWire();
        await using var source = wire.CreateSource();
        using var connection = await source.OpenConnectionAsync(Token);
        using var command = connection.CreateCommand("select $1");
        command.Parameters.Add(MpgsqlParameter.Int64(7));
        var opening = command.ExecuteReaderAsync(Token);
        Assert.Equal(Expected("select $1", 7), await wire.ReadOutputAsync());
        await wire.WriteAsync(Join(Begin(20), MetadataSeen));
        await WaitForMetadataAsync(wire.Session);
        Assert.False(opening.IsCompleted);
        var writing = wire.WriteAsync(Join(Error("22012"), Ready()));
        var error = await Assert.ThrowsAsync<MpgsqlPostgresException>(() => opening.WaitAsync(TestTimeout, Token));
        Assert.Equal("22012", error.SqlState);
        Assert.Equal(0, error.QueryIndex);
        Assert.Equal(TransactionStatus.Idle, error.TransactionStatus);
        await writing.WaitAsync(TestTimeout, Token);
        Assert.True(wire.Session.IsIdleAndHealthy);
        command.Parameters[0].Value = 9L;
        await ReadNextValueAsync(wire, command, 9);
    }

    [Fact]
    public async Task CancellationDuringFirstRowPrefetchFinishesRecoveryBeforeFollowingExecution()
    {
        await using var wire = new FusedWire();
        var cancelSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancels = 0;
        await using var source = wire.CreateSource(() => { Interlocked.Increment(ref cancels); cancelSent.TrySetResult(); });
        using var connection = await source.OpenConnectionAsync(Token);
        using var command = connection.CreateCommand("select $1");
        command.Parameters.Add(MpgsqlParameter.Int64(7));
        using var request = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var opening = command.ExecuteReaderAsync(request.Token);
        Assert.Equal(Expected("select $1", 7), await wire.ReadOutputAsync());
        await wire.WriteAsync(Join(Begin(20), MetadataSeen));
        await WaitForMetadataAsync(wire.Session);
        Assert.False(opening.IsCompleted);
        request.Cancel();
        await cancelSent.Task.WaitAsync(TestTimeout, Token);
        var writing = wire.WriteAsync(Join(Error("57014"), Ready()));
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TestTimeout, Token));
        Assert.Equal(request.Token, error.CancellationToken);
        await writing.WaitAsync(TestTimeout, Token);
        Assert.True(wire.Session.IsIdleAndHealthy);
        command.Parameters[0].Value = 9L;
        await ReadNextValueAsync(wire, command, 9);
        Assert.Equal(1, cancels);
    }

    [Fact]
    public async Task NonRowFirstResultPreservesFollowingRowsAndPerCommandAffectedCounts()
    {
        await using var wire = new FusedWire();
        await using var source = wire.CreateSource();
        using var connection = await source.OpenConnectionAsync(Token);
        using var batch = connection.CreateBatch();
        var update = new MpgsqlBatchCommand("update empty");
        var select = new MpgsqlBatchCommand("select $1");
        select.Parameters.Add(MpgsqlParameter.Int64(9));
        batch.BatchCommands.Add(update);
        batch.BatchCommands.Add(select);
        var opening = batch.ExecuteReaderAsync(Token);
        var expected = Join(Expected("update empty", sync: false), Expected("select $1", 9));
        Assert.Equal(expected, await wire.ReadOutputAsync());
        var writing = wire.WriteAsync(Join(Packet('1'), Packet('2'), Packet('n'), Command("UPDATE 0"), Query(9), Ready()));
        using var reader = await opening.WaitAsync(TestTimeout, Token);
        Assert.False(reader.HasRows);
        Assert.Equal(0, reader.FieldCount);
        Assert.False(await reader.ReadAsync(Token));
        Assert.True(await reader.NextResultAsync(Token));
        Assert.True(reader.HasRows);
        Assert.True(await reader.ReadAsync(Token));
        Assert.Equal(9L, reader.GetInt64(0));
        Assert.False(await reader.ReadAsync(Token));
        Assert.False(await reader.NextResultAsync(Token));
        await writing.WaitAsync(TestTimeout, Token);
        Assert.Equal(0, update.RecordsAffected64);
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    [Fact]
    public async Task PublicRawReaderStillReturnsMetadataBeforeItsFirstRowArrives()
    {
        await using var wire = new ScriptedSession();
        await using var batch = wire.Session.CreateBatch(Token);
        await batch.SendQueryAsync("select 7");
        await batch.SendSyncAsync();
        var opening = batch.ReadResultsAsync().AsTask();
        await wire.WriteAsync(Begin(20));
        await using var reader = await opening.WaitAsync(TestTimeout, Token);
        Assert.Equal(20U, reader.Columns.Span[0].DataTypeOid);
        Assert.Throws<InvalidOperationException>(() => reader.GetRawValue(0));
        var writing = wire.WriteAsync(Join(Row(Int64(7)), Command(), Ready()));
        Assert.True(await reader.ReadAsync());
        Assert.Equal(7L, reader.GetInt64(0));
        Assert.False(await reader.ReadAsync());
        Assert.False(await reader.NextResultAsync());
        await writing.WaitAsync(TestTimeout, Token);
    }

    private static async Task ReadNextValueAsync(FusedWire wire, MpgsqlCommand command, long value)
    {
        var opening = command.ExecuteReaderAsync(Token);
        Assert.Equal(Expected(command.CommandText, value), await wire.ReadOutputAsync());
        var writing = wire.WriteAsync(Join(Query(value), Ready()));
        using var reader = await opening.WaitAsync(TestTimeout, Token);
        Assert.True(await reader.ReadAsync(Token));
        Assert.Equal(value, reader.GetInt64(0));
        Assert.False(await reader.ReadAsync(Token));
        Assert.False(await reader.NextResultAsync(Token));
        await writing.WaitAsync(TestTimeout, Token);
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    private static byte[] Expected(string sql, long? value = null, bool sync = true)
    {
        var output = new ArrayBufferWriter<byte>();
        FrontendMessage.Parse(sql, parameterTypes: value.HasValue ? new uint[] { 20 } : ReadOnlyMemory<uint>.Empty).Write(output);
        FrontendMessage.Bind(parameters: value.HasValue ? new ReadOnlyMemory<byte>?[] { Int64(value.Value) } : ReadOnlyMemory<ReadOnlyMemory<byte>?>.Empty,
            parameterFormats: new[] { FormatCode.Binary }, resultFormats: new[] { FormatCode.Binary }).Write(output);
        FrontendMessage.Describe(StatementOrPortal.Portal).Write(output);
        FrontendMessage.Execute().Write(output);
        if (sync)
            FrontendMessage.Sync().Write(output);
        return output.WrittenSpan.ToArray();
    }

    private static async Task WaitForMetadataAsync(MpgsqlMessageSession session)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TestTimeout);
        while (!session.TryGetParameter("fused_metadata", out var seen) || seen != "seen")
            await Task.Delay(1, deadline.Token);
    }

    private sealed class FusedWire : IAsyncDisposable
    {
        private readonly Pipe _incoming = new(new PipeOptions(pauseWriterThreshold: 32, resumeWriterThreshold: 16, useSynchronizationContext: false));
        private readonly Pipe _outgoing = new(new PipeOptions(pauseWriterThreshold: 0, useSynchronizationContext: false));
        internal CompletionReader Reader { get; }
        internal MpgsqlMessageSession Session { get; }
        internal FusedWire()
        {
            Reader = new CompletionReader(_incoming.Reader);
            Session = new MpgsqlMessageSession(Reader, _outgoing.Writer, Token);
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

    private sealed class CompletionReader(PipeReader inner) : PipeReader
    {
        private int _pending, _completeCalls;
        internal bool CompletedWithPendingRead;
        internal int CompleteCalls => Volatile.Read(ref _completeCalls);
        public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _pending);
            try { return await inner.ReadAsync(cancellationToken).ConfigureAwait(false); }
            finally { Interlocked.Decrement(ref _pending); }
        }
        public override void AdvanceTo(SequencePosition consumed) => inner.AdvanceTo(consumed);
        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) => inner.AdvanceTo(consumed, examined);
        public override bool TryRead(out ReadResult result) => inner.TryRead(out result);
        public override void CancelPendingRead() => inner.CancelPendingRead();
        private void Completing()
        {
            CompletedWithPendingRead |= Volatile.Read(ref _pending) != 0;
            Interlocked.Increment(ref _completeCalls);
        }
        public override void Complete(Exception? exception = null) { Completing(); inner.Complete(exception); }
        public override ValueTask CompleteAsync(Exception? exception = null) { Completing(); return inner.CompleteAsync(exception); }
    }
}
