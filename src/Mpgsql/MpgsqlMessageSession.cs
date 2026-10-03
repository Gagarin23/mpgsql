using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipelines;
using System.Threading.Channels;
using Mpgsql.Internal;
using Mpgsql.Protocol;

namespace Mpgsql;

/// <summary>Exclusive I/O owner of authenticated protocol-3.0, UTF8 input/output pipes.</summary>
/// <remarks>
/// Use separate sending/reading flows. SendQueryAsync queues writes in invocation order; SendSyncAsync
/// belongs to the sending flow. ReadResultsAsync never writes. One group collects at a time, with several
/// sealed groups in flight. Dispose completes both endpoints; adapters control underlying stream ownership.
/// </remarks>
public sealed class MpgsqlMessageSession : IAsyncDisposable
{
    private readonly PipeReader _input;
    private readonly PipeWriter _output;
    private readonly CancellationTokenSource _lifetime;
    private readonly Lock _gate = new();
    private readonly Queue<MpgsqlQueryBatch> _responses = new();
    private readonly HashSet<MpgsqlQueryBatch> _batches = [];
    private readonly ConcurrentDictionary<string, MpgsqlPreparedStatement> _statements = new(StringComparer.Ordinal);
    private readonly BackendFrameBuffer _frames = new();

    private readonly Channel<OutboundWork> _writes = Channel.CreateUnbounded<OutboundWork>(new()
        {SingleReader = true, AllowSynchronousContinuations = false});

    private readonly Channel<MpgsqlQueryBatch> _cancellations = Channel.CreateUnbounded<MpgsqlQueryBatch>(new()
        {SingleReader = true, AllowSynchronousContinuations = false});

    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<DiagnosticMessage> _notices = new();
    private readonly ConcurrentQueue<NotificationResponse> _notifications = new();
    private readonly ConcurrentDictionary<string, string> _parameters = new(StringComparer.Ordinal);
    private readonly Task _receiveTask;
    private readonly Task _writeTask;
    private readonly Task _controlTask;
    private MpgsqlQueryBatch? _collecting;
    private Exception? _failure;
    private int _disposed;
    private long _copiedRows;
    private long _nextStatement;
    private RowBufferBudget? _rowBudget;
    private bool _claimed;
    private TransactionStatus _transactionStatus = TransactionStatus.Idle;

    internal bool IsHealthy
    {
        get { lock (_gate) return _failure is null && _disposed == 0 && !_lifetime.IsCancellationRequested; }
    }
    internal TransactionStatus LastTransactionStatus { get { lock (_gate) return _transactionStatus; } }
    internal bool IsClaimedForDataSource { get { lock (_gate) return _claimed; } }
    internal long BufferedRowBytes => _rowBudget?.Used ?? 0;
    internal void WakeRowBudget() => _rowBudget?.Pulse();

    internal void ClaimForDataSource(long rowBytes)
    {
        lock (_gate)
        {
            ThrowIfStopped();
            if (_claimed || _collecting is not null || _responses.Count != 0 || _batches.Count != 0
                || _transactionStatus != TransactionStatus.Idle)
                throw new InvalidOperationException("The factory must return an idle, exclusively owned session.");
            _claimed = true;
            _rowBudget = new(rowBytes);
        }
    }

    public MpgsqlMessageSession(
        PipeReader input,
        PipeWriter output,
        CancellationToken lifetimeToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        _input = input;
        _output = output;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        _controlTask = ControlLoopAsync();
        _writeTask = WriteLoopAsync();
        _receiveTask = ReceiveLoopAsync();
    }

    /// <summary>Faults on transport/protocol failure; completes normally on disposal.</summary>
    public Task Completion => _completion.Task;
    public bool TryReadNotice(out DiagnosticMessage notice) => _notices.TryDequeue(out notice);
    public bool TryReadNotification(out NotificationResponse notification) => _notifications.TryDequeue(out notification);
    public bool TryGetParameter(string name,
        out string? value) => _parameters.TryGetValue(name,
        out value);

    public MpgsqlQueryBatch CreateBatch(CancellationToken requestToken = default)
    {
        lock (_gate)
        {
            ThrowIfStopped();
            var batch = new MpgsqlQueryBatch(this,
                requestToken);
            _batches.Add(batch);
            return batch;
        }
    }

    /// <summary>Creates a local statement with an owned, complete list of nonzero parameter OIDs.</summary>
    /// <remarks>No messages are sent. Queue SendPrepareAsync in a batch and explicitly send Sync.</remarks>
    public MpgsqlPreparedStatement CreatePreparedStatement(string sql,
        ReadOnlyMemory<uint> parameterTypes = default)
    {
        lock (_gate)
        {
            ThrowIfStopped();
            long number = checked(++_nextStatement);
            var statement = new MpgsqlPreparedStatement(this,
                "mpgsql_ps_" + number.ToString(CultureInfo.InvariantCulture), sql, parameterTypes);
            _statements.TryAdd(statement.Name, statement);
            return statement;
        }
    }

    internal void ReleaseStatement(MpgsqlPreparedStatement statement)
        => _statements.TryRemove(statement.Name, out _);

    internal long CopiedRowBytes => Interlocked.Read(ref _copiedRows) + Interlocked.Read(ref _frames.CopiedRowBytes);
    internal void RecordRowCopy(long size) => Interlocked.Add(ref _copiedRows,
        size);
    internal void ScheduleDiscard(MpgsqlQueryBatch batch) => _cancellations.Writer.TryWrite(batch);
    internal void ReleaseBatch(MpgsqlQueryBatch batch)
    {
        if (!batch.Completion.IsCompleted || !batch.ConsumerDisposed)
        {
            return;
        }
        batch.ReleaseRegistration();
        lock (_gate) _batches.Remove(batch);
    }

    internal ValueTask SendQueryAsync(MpgsqlQueryBatch batch,
        string sql,
        ReadOnlyMemory<MpgsqlParameter> parameters)
    {
        int size = QueryPacket.GetByteCount(sql,
            parameters.Span);
        lock (_gate)
        {
            CheckSend(batch);
            var work = new OutboundWork(batch,
                sql,
                parameters,
                size);
            return QueueWrite(work);
        }
    }

    internal ValueTask SendPrepareAsync(MpgsqlQueryBatch batch, MpgsqlPreparedStatement statement)
    {
        lock (_gate)
        {
            CheckSend(batch);
            CheckStatement(statement);
            statement.QueuePrepare(batch);
            return QueueWrite(new(batch, MessageOperationKind.Prepare, statement, statement.ParseMessage.GetByteCount()));
        }
    }

    internal ValueTask SendQueryAsync(MpgsqlQueryBatch batch, MpgsqlPreparedStatement statement,
        ReadOnlyMemory<MpgsqlParameter> parameters)
    {
        CheckStatement(statement);
        // Payload validation can scan large arrays; keep it outside the session admission gate.
        int size = QueryPacket.GetPreparedByteCount(statement.Name, parameters.Span);
        lock (_gate)
        {
            CheckSend(batch);
            statement.ValidateExecution(batch, parameters.Span);
            return QueueWrite(new(batch, MessageOperationKind.PreparedQuery, statement, size, parameters));
        }
    }

    internal ValueTask SendCloseAsync(MpgsqlQueryBatch batch, MpgsqlPreparedStatement statement)
    {
        lock (_gate)
        {
            CheckSend(batch);
            CheckStatement(statement);
            if (!statement.QueueClose(batch))
                return ValueTask.CompletedTask;
            return QueueWrite(new(batch, MessageOperationKind.Close, statement, statement.CloseMessage.GetByteCount()));
        }
    }

    private void CheckSend(MpgsqlQueryBatch batch)
    {
        ThrowIfStopped();
        batch.ThrowForSend();
        if (_collecting is not null && _collecting != batch)
            throw new InvalidOperationException("Call SendSyncAsync on the collecting group before sending another group.");
    }

    private void CheckStatement(MpgsqlPreparedStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        if (statement.Session != this)
            throw new ArgumentException("The prepared statement belongs to another session.", nameof(statement));
    }

    // Called with the session gate held, after validation and statement admission.
    private ValueTask QueueWrite(OutboundWork work)
    {
        if (_collecting is null)
        {
            _collecting = work.Batch;
            _responses.Enqueue(work.Batch);
        }
        work.Batch.AddWrite(work);
        _writes.Writer.TryWrite(work);
        return new(work.Completion);
    }

    internal ValueTask SendSyncAsync(MpgsqlQueryBatch batch)
    {
        lock (_gate)
        {
            ThrowIfStopped();
            if (_collecting is not null && _collecting != batch)
            {
                throw new InvalidOperationException("Another group owns the collecting boundary.");
            }
            batch.SyncQueued();
            if (_collecting is null)
            {
                _responses.Enqueue(batch);
            }
            _collecting = null;
            var work = new OutboundWork(batch);
            batch.AddWrite(work);
            _writes.Writer.TryWrite(work);
            return new(work.Completion);
        }
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var work in _writes.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (!work.TryStart())
                {
                    work.Batch.RemoveWrite(work);
                    continue;
                }
                try
                {
                    lock (_gate) ThrowIfStopped();
                    if (work.IsSync)
                    {
                        FrontendMessage.Sync().Write(_output);
                        work.Batch.SealPublished();
                    }
                    else
                    {
                        work.Batch.RequestToken.ThrowIfCancellationRequested();
                        work.Write(_output.GetSpan(work.Size));
                        work.Batch.RequestToken.ThrowIfCancellationRequested();
                        lock (_gate)
                        {
                            ThrowIfStopped();
                            work.Batch.RegisterOperation(work.Kind, work.Statement);
                            _output.Advance(work.Size);
                        }
                    }
                    work.Published();
                    var flush = await _output.FlushAsync(_lifetime.Token).ConfigureAwait(false);
                    if (flush.IsCanceled)
                    {
                        throw new OperationCanceledException(_lifetime.Token);
                    }
                    if (flush.IsCompleted)
                    {
                        throw new IOException("The output transport stopped reading.");
                    }
                    work.Complete();
                }
                catch (OperationCanceledException error) when (!work.IsSync && work.Batch.RequestToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
                {
                    work.Complete(error);
                }
                catch (Exception error)
                {
                    work.Complete(error);
                    Fail(error);
                }
                finally { work.Batch.RemoveWrite(work); }
            }
        }
        catch (Exception error)
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                Fail(error);
            }
        }
        finally { await _output.CompleteAsync(_failure).ConfigureAwait(false); }
    }

    private async Task ControlLoopAsync()
    {
        await foreach (var batch in _cancellations.Reader.ReadAllAsync().ConfigureAwait(false))
            batch.ProcessCancellation();
    }

    private async Task ReceiveLoopAsync()
    {
        Exception? failure = null;
        try
        {
            while (true)
            {
                var read = await _input.ReadAsync(_lifetime.Token).ConfigureAwait(false);
                var input = read.Buffer;
                try
                {
                    if (read.IsCanceled)
                    {
                        throw new OperationCanceledException(_lifetime.Token);
                    }
                    while (!input.IsEmpty)
                    {
                        MpgsqlQueryBatch? batch;
                        lock (_gate) batch = _responses.TryPeek(out var head) ? head : null;
                        if (!_frames.TryRead(ref input,
                                batch?.DiscardsRows == true,
                                out var message,
                                out var owner,
                                out int skippedColumns))
                        {
                            break;
                        }
                        try
                        {
                            if (skippedColumns >= 0)
                            {
                                (batch ?? throw new InvalidDataException("DataRow without an active group.")).AcceptSkippedRow(skippedColumns);
                                continue;
                            }
                            if (message.IsAsynchronous)
                            {
                                RouteAsynchronous(message);
                                continue;
                            }
                            if (message.Kind is BackendMessageKind.CopyInResponse or BackendMessageKind.CopyOutResponse or BackendMessageKind.CopyBothResponse)
                            {
                                throw new NotSupportedException("COPY requires the separate COPY API and an exclusively owned connection.");
                            }
                            if (batch is null)
                            {
                                throw new InvalidDataException($"Unexpected idle backend message: {message.Kind}.");
                            }
                            RowBufferBudget? reservation = null;
                            try
                            {
                                if (message.Kind == BackendMessageKind.DataRow && _rowBudget is { } budget
                                    && await budget.ReserveAsync(message.Payload.Length, batch, _lifetime.Token).ConfigureAwait(false))
                                    reservation = budget;
                                if (message.Kind == BackendMessageKind.ReadyForQuery)
                                    lock (_gate) _transactionStatus = message.GetTransactionStatus();
                                batch.Accept(message, ref owner, ref reservation);
                            }
                            finally { reservation?.Release(message.Payload.Length); }
                            if (message.Kind == BackendMessageKind.ReadyForQuery)
                            {
                                lock (_gate) _responses.Dequeue();
                                ReleaseBatch(batch);
                            }
                        }
                        finally { owner?.Dispose(); }
                    }
                    if (read.IsCompleted)
                    {
                        throw new EndOfStreamException(_frames.HasPartialFrame ? "Truncated backend frame." : "The backend closed the transport.");
                    }
                }
                finally
                {
                    _input.AdvanceTo(input.Start,
                        read.Buffer.End);
                }
            }
        }
        catch (Exception error)
        {
            failure = error;
            if (Volatile.Read(ref _disposed) == 0)
            {
                Fail(error);
            }
        }
        finally
        {
            _frames.Dispose();
            _cancellations.Writer.TryComplete();
            await _input.CompleteAsync(Volatile.Read(ref _disposed) == 0 ? failure : null).ConfigureAwait(false);
        }
    }

    private void RouteAsynchronous(BackendMessage message)
    {
        switch (message.Kind)
        {
            case BackendMessageKind.NoticeResponse: _notices.Enqueue(message.GetDiagnostics()); break;
            case BackendMessageKind.NotificationResponse: _notifications.Enqueue(message.GetNotification()); break;
            case BackendMessageKind.ParameterStatus:
                var parameter = message.GetParameterStatus();
                if (parameter.Name == "client_encoding" && parameter.Value != "UTF8")
                {
                    throw new NotSupportedException("MpgsqlMessageSession requires client_encoding=UTF8.");
                }
                _parameters[parameter.Name] = parameter.Value;
                break;
        }
    }

    private void ThrowIfStopped()
    {
        if (_failure is { } failure)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        }
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(MpgsqlMessageSession));
        }
        _lifetime.Token.ThrowIfCancellationRequested();
    }

    private void Fail(Exception error)
    {
        MpgsqlQueryBatch[] batches;
        MpgsqlPreparedStatement[] statements;
        lock (_gate)
        {
            if (_failure is not null || Volatile.Read(ref _disposed) != 0)
            {
                return;
            }
            _failure = error;
            batches = [.. _batches.Where(batch => !batch.Completion.IsCompleted)];
            statements = [.. _statements.Values];
            _statements.Clear();
            _writes.Writer.TryComplete();
        }
        foreach (var statement in statements) statement.FailPreparation(error);
        foreach (var batch in batches)
        {
            batch.Fail(error);
            ReleaseBatch(batch);
        }
        _completion.TrySetException(error);
        _lifetime.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed,
                1) != 0)
        {
            return;
        }
        MpgsqlQueryBatch[] batches;
        MpgsqlPreparedStatement[] statements;
        var error = new ObjectDisposedException(nameof(MpgsqlMessageSession));
        lock (_gate)
        {
            batches = [.. _batches];
            statements = [.. _statements.Values];
            _statements.Clear();
            _writes.Writer.TryComplete();
        }
        foreach (var statement in statements) statement.FailPreparation(error);
        foreach (var batch in batches)
        {
            batch.Fail(error);
            batch.ReleaseRegistration();
        }
        _lifetime.Cancel();
        // ReadAsync/FlushAsync already use this token. Calling CancelPending* after a stream
        // adapter concurrently completed can touch its disposed internal cancellation source.
        await _receiveTask.ConfigureAwait(false);
        await _writeTask.ConfigureAwait(false);
        await _controlTask.ConfigureAwait(false);
        _completion.TrySetResult();
        _lifetime.Dispose();
    }
}
