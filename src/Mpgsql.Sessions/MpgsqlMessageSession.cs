using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Mpgsql.Internal;
using Mpgsql.Protocol;

namespace Mpgsql;

/// <summary>Exclusive I/O owner of authenticated protocol-3.0, UTF8 input/output pipes.</summary>
/// <remarks>
///     Use separate sending/reading flows. SendQueryAsync queues writes in invocation order; SendSyncAsync
///     belongs to the sending flow. ReadResultsAsync never writes. One group collects at a time, with several
///     sealed groups in flight. Dispose completes both endpoints; adapters control underlying stream ownership.
/// </remarks>
public sealed partial class MpgsqlMessageSession : IAsyncDisposable
{
    private readonly HashSet<MpgsqlQueryBatch> _batches = [];

    private readonly Channel<MpgsqlQueryBatch> _cancellations = Channel.CreateUnbounded<MpgsqlQueryBatch>
    (
        new UnboundedChannelOptions
        {
            SingleReader = true,
            AllowSynchronousContinuations = false
        }
    );

    // Only the receive loop owns this bounded wave; slots are cleared before notification.
    private readonly MpgsqlQueryBatch?[] _completedNotifications = new MpgsqlQueryBatch?[64];

    private readonly TaskCompletionSource _completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _controlTask;
    private readonly BackendFrameBuffer _frames = new BackendFrameBuffer();
    private readonly Lock _gate = new Lock();
    private readonly PipeReader _input;

    private readonly CancellationTokenSource _lifetime;

    // Weak ownership keeps live local handles observable on session shutdown without rooting SQL.
    private readonly ConditionalWeakTable<MpgsqlPreparedStatement, MpgsqlMessageSession> _localStatements = new ConditionalWeakTable<MpgsqlPreparedStatement, MpgsqlMessageSession>();
    private readonly BackendMetadataCache _metadata = new BackendMetadataCache();
    private readonly ConcurrentQueue<DiagnosticMessage> _notices = new ConcurrentQueue<DiagnosticMessage>();
    private readonly ConcurrentQueue<NotificationResponse> _notifications = new ConcurrentQueue<NotificationResponse>();
    private readonly PipeWriter _output;
    private readonly ConcurrentDictionary<string, string> _parameters = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
    private readonly Task _receiveTask;
    private readonly Queue<MpgsqlQueryBatch> _responses = new Queue<MpgsqlQueryBatch>();

    private readonly RowStoragePool _rows = new RowStoragePool();

    // Only admitted preparations and confirmed server statements require strong ownership.
    private readonly ConcurrentDictionary<string, MpgsqlPreparedStatement> _statements = new ConcurrentDictionary<string, MpgsqlPreparedStatement>(StringComparer.Ordinal);
    private readonly Task _writeTask;

    private readonly Channel<OutboundWork> _writes = Channel.CreateUnbounded<OutboundWork>
    (
        new UnboundedChannelOptions
        {
            SingleReader = true,
            AllowSynchronousContinuations = false
        }
    );

    private bool _claimed;
    private MpgsqlQueryBatch? _collecting;
    private int _completedNotificationCount;
    private long _copiedRows;
    private int _disposed;
    private Exception? _failure;
    private long _nextStatement;
    private RowBufferBudget? _rowBudget;
    private TransactionStatus _transactionStatus = TransactionStatus.Idle;
    private DiagnosticMessage? _unrecoveredError;

    public MpgsqlMessageSession(
        PipeReader input,
        PipeWriter output,
        CancellationToken lifetimeToken = default
    )
        : this(input, output, lifetimeToken, null) { }

    private MpgsqlMessageSession(
        PipeReader input, PipeWriter output,
        CancellationToken lifetimeToken, SocketTransport? transport
    )
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        _input = input;
        _output = output;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        _transport = transport;
        if (transport is not null)
        {
            _rowBudget = new RowBufferBudget(8 * 1024 * 1024);
            foreach (var parameter in transport.Parameters)
            {
                _parameters[parameter.Key] = parameter.Value;
            }
            foreach (var notice in transport.Notices)
            {
                _notices.Enqueue(notice);
            }
        }
        _controlTask = ControlLoopAsync();
        _writeTask = WriteLoopAsync();
        _receiveTask = ReceiveLoopAsync();
    }

    internal bool IsHealthy
    {
        get
        {
            lock (_gate)
            {
                return _failure is null && _disposed == 0 && !_lifetime.IsCancellationRequested;
            }
        }
    }

    internal TransactionStatus LastTransactionStatus
    {
        get
        {
            lock (_gate)
            {
                return _transactionStatus;
            }
        }
    }

    internal bool IsIdleAndHealthy
    {
        get
        {
            lock (_gate)
            {
                return _failure is null && _disposed == 0 && !_lifetime.IsCancellationRequested
                       && _transactionStatus == TransactionStatus.Idle;
            }
        }
    }

    internal bool IsClaimedForDataSource
    {
        get
        {
            lock (_gate)
            {
                return _claimed;
            }
        }
    }

    internal long BufferedRowBytes => _rowBudget?.Used ?? 0;
    internal int TrackedStatementCount => _statements.Count;

    /// <summary>Faults on transport/protocol failure; completes normally on disposal.</summary>
    public Task Completion => _completion.Task;

    internal long CopiedRowBytes => Interlocked.Read(ref _copiedRows) + Interlocked.Read(ref _frames.CopiedRowBytes);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange
            (
                ref _disposed,
                1
            ) != 0)
        {
            return;
        }
        MpgsqlQueryBatch[] batches;
        MpgsqlPreparedStatement[] statements;
        var error = new ObjectDisposedException(nameof(MpgsqlMessageSession));
        lock (_gate)
        {
            batches = [.. _batches];
            statements = [.. _statements.Values, .. _localStatements.Select(pair => pair.Key)];
            _statements.Clear();
            _localStatements.Clear();
            _writes.Writer.TryComplete();
        }
        foreach (var statement in statements)
        {
            statement.FailPreparation(error);
        }
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
        _transport?.Dispose();
    }
    internal void WakeRowBudget()
    {
        _rowBudget?.Pulse();
    }
    internal ReadOnlyMemory<RowField> DecodeRowDescription(BackendMessage message)
    {
        return _metadata.RowDescription(message);
    }
    internal string DecodeCommandTag(BackendMessage message)
    {
        return _metadata.CommandTag(message);
    }
    internal OwnedRow OwnRow(
        BackendMessage message, IMemoryOwner<byte>? owner,
        RowBufferBudget? budget
    )
    {
        return _rows.Rent(message, owner, budget);
    }

    internal void ClaimForDataSource(long rowBytes)
    {
        lock (_gate)
        {
            ThrowIfStopped();
            if (_claimed || _collecting is not null || _responses.Count != 0 || _batches.Count != 0
                || _transactionStatus != TransactionStatus.Idle)
            {
                throw new InvalidOperationException("The factory must return an idle, exclusively owned session.");
            }
            _claimed = true;
            _rowBudget = new RowBufferBudget(rowBytes);
        }
    }
    public bool TryReadNotice(out DiagnosticMessage notice)
    {
        return _notices.TryDequeue(out notice);
    }
    public bool TryReadNotification(out NotificationResponse notification)
    {
        return _notifications.TryDequeue(out notification);
    }
    public bool TryGetParameter(
        string name,
        out string? value
    )
    {
        return _parameters.TryGetValue
        (
            name,
            out value
        );
    }

    public MpgsqlQueryBatch CreateBatch(CancellationToken requestToken = default)
    {
        lock (_gate)
        {
            ThrowIfStopped();
            var batch = new MpgsqlQueryBatch
            (
                this,
                requestToken
            );
            _batches.Add(batch);
            return batch;
        }
    }

    internal MpgsqlQueryBatch CreateSharedSyncBatch()
    {
        lock (_gate)
        {
            ThrowIfStopped();
            var batch = new MpgsqlQueryBatch(this, CancellationToken.None, true);
            _batches.Add(batch);
            return batch;
        }
    }

    /// <summary>Creates a local statement with an owned, complete list of nonzero parameter OIDs.</summary>
    /// <remarks>
    ///     No messages are sent and the session does not keep an unused handle alive.
    ///     Queue SendPrepareAsync in a batch and explicitly send Sync.
    /// </remarks>
    public MpgsqlPreparedStatement CreatePreparedStatement(
        string sql,
        ReadOnlyMemory<uint> parameterTypes = default
    )
    {
        lock (_gate)
        {
            ThrowIfStopped();
            var number = checked(++_nextStatement);
            var statement = new MpgsqlPreparedStatement
            (
                this,
                "mpgsql_ps_" + number.ToString(CultureInfo.InvariantCulture), sql, parameterTypes
            );
            _localStatements.Add(statement, this);
            return statement;
        }
    }

    internal void ReleaseStatement(MpgsqlPreparedStatement statement)
    {
        _statements.TryRemove(statement.Name, out _);
        _localStatements.Remove(statement);
    }
    internal void RecordRowCopy(long size)
    {
        Interlocked.Add
        (
            ref _copiedRows,
            size
        );
    }
    internal void ScheduleDiscard(MpgsqlQueryBatch batch)
    {
        _cancellations.Writer.TryWrite(batch);
    }
    internal void ReleaseBatch(MpgsqlQueryBatch batch)
    {
        if (!batch.Completion.IsCompleted || !batch.ConsumerDisposed)
        {
            return;
        }
        batch.ReleaseRegistration();
        lock (_gate)
        {
            _batches.Remove(batch);
        }
    }

    internal ValueTask SendQueryAsync(
        MpgsqlQueryBatch batch,
        string sql,
        ReadOnlyMemory<MpgsqlParameterValue> parameters
    )
    {
        return SendQueryAsync(batch, new QueryDefinition(sql, parameters));
    }

    internal ValueTask SendQueryAsync(MpgsqlQueryBatch batch, QueryDefinition query)
    {
        var size = query.EncodedSize != 0 ? query.EncodedSize : QueryPacket.GetByteCount(query.Sql, query.Parameters.Span);
        lock (_gate)
        {
            CheckSend(batch);
            var work = new OutboundWork
            (
                batch,
                query.Sql,
                query.Parameters,
                size
            );
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
            _statements.TryAdd(statement.Name, statement);
            _localStatements.Remove(statement);
            return QueueWrite(new OutboundWork(batch, MessageOperationKind.Prepare, statement, statement.ParseMessage.GetByteCount()));
        }
    }

    // All commands have been frozen and validated before the first work enters the writer FIFO.
    // Raw SendQueryAsync retains its individual acknowledgement and explicit producer API.
    internal Task SendQueriesAsync(MpgsqlQueryBatch batch, QueryDefinition[] queries)
    {
        for (var i = 0;
             i < queries.Length;
             i++)
        {
            if (queries[i].EncodedSize == 0)
            {
                queries[i] = queries[i] with
                {
                    EncodedSize = queries[i]
                        .Measure()
                };
            }
        }
        lock (_gate)
        {
            CheckSend(batch);
            return queries.Length == 0
                ? Task.CompletedTask
                : QueueWrite(new OutboundWork(batch, queries))
                    .AsTask();
        }
    }

    // The upper API owns the complete boundary. Admit query bytes and its mandatory Sync as
    // one FIFO work item, retaining independent logical-cancellation and delivery barriers.
    internal OutboundWork SendExecution(
        MpgsqlQueryBatch batch, QueryDefinition single,
        QueryDefinition[]? queries
    )
    {
        if (queries is null)
        {
            if (single.EncodedSize == 0)
            {
                single = single with
                {
                    EncodedSize = single.Measure()
                };
            }
        }
        else
        {
            for (var i = 0;
                 i < queries.Length;
                 i++)
            {
                if (queries[i].EncodedSize == 0)
                {
                    queries[i] = queries[i] with
                    {
                        EncodedSize = queries[i]
                            .Measure()
                    };
                }
            }
        }
        lock (_gate)
        {
            CheckSend(batch);
            if (queries is null && single.PreparedStatement is { } prepared)
            {
                CheckStatement(prepared);
                prepared.ValidateExecution(batch, single.Parameters.Span);
            }
            else if (queries is not null)
            {
                foreach (ref readonly var query in queries.AsSpan())
                {
                    if (query.PreparedStatement is { } statement)
                    {
                        CheckStatement(statement);
                        statement.ValidateExecution(batch, query.Parameters.Span);
                    }
                }
            }
            var work = queries is null ? new OutboundWork(batch, single) : new OutboundWork(batch, queries, true);
            batch.SyncQueued();
            _ = QueueWrite(work);
            _collecting = null;
            return work;
        }
    }

    internal OutboundWork SendGroupedExecution(
        MpgsqlQueryBatch request, MpgsqlQueryBatch boundary,
        QueryDefinition query
    )
    {
        if (query.EncodedSize == 0)
        {
            query = query with
            {
                EncodedSize = QueryPacket.GetByteCount(query.Sql, query.Parameters.Span)
            };
        }
        lock (_gate)
        {
            CheckSend(boundary);
            request.ThrowForSend();
            var work = new OutboundWork(request, boundary, query);
            request.SyncQueued();
            boundary.SyncGroup.Add(request);
            _ = QueueWrite(work);
            return work;
        }
    }

    internal ValueTask SendQueryAsync(
        MpgsqlQueryBatch batch, MpgsqlPreparedStatement statement,
        ReadOnlyMemory<MpgsqlParameterValue> parameters
    )
    {
        CheckStatement(statement);
        // Payload validation can scan large arrays; keep it outside the session admission gate.
        var size = QueryPacket.GetPreparedByteCount(statement.Name, parameters.Span);
        lock (_gate)
        {
            CheckSend(batch);
            statement.ValidateExecution(batch, parameters.Span);
            return QueueWrite(new OutboundWork(batch, MessageOperationKind.PreparedQuery, statement, size, parameters));
        }
    }

    internal ValueTask SendCloseAsync(MpgsqlQueryBatch batch, MpgsqlPreparedStatement statement)
    {
        lock (_gate)
        {
            CheckSend(batch);
            CheckStatement(statement);
            if (!statement.QueueClose(batch))
            {
                return ValueTask.CompletedTask;
            }
            return QueueWrite(new OutboundWork(batch, MessageOperationKind.Close, statement, statement.CloseMessage.GetByteCount()));
        }
    }

    private void CheckSend(MpgsqlQueryBatch batch)
    {
        ThrowIfStopped();
        batch.ThrowForSend();
        if (_collecting is not null && _collecting != batch)
        {
            throw new InvalidOperationException("Call SendSyncAsync on the collecting group before sending another group.");
        }
    }

    private void CheckStatement(MpgsqlPreparedStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        if (statement.Session != this)
        {
            throw new ArgumentException("The prepared statement belongs to another session.", nameof(statement));
        }
    }

    // Called with the session gate held, after validation and statement admission.
    private ValueTask QueueWrite(OutboundWork work)
    {
        if (_collecting is null)
        {
            _collecting = work.ResponseBatch;
            _responses.Enqueue(work.ResponseBatch);
        }
        work.Batch.AddWrite(work);
        _writes.Writer.TryWrite(work);
        return new ValueTask(work.Completion);
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
            return new ValueTask(work.Completion);
        }
    }

    internal void SendSharedSync(SharedSyncGroup group)
    {
        lock (_gate)
        {
            ThrowIfStopped();
            var batch = group.Boundary;
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
            var work = group.SyncWork;
            batch.AddWrite(work);
            _writes.Writer.TryWrite(work);
        }
    }

    internal void Abort(Exception error)
    {
        _transport?.Dispose();
        Fail(error);
    }

    private async Task WriteLoopAsync()
    {
        // Batch only work already queued. A lone awaited send must flush without waiting for Sync.
        // Bound each drain so sustained producers still yield to transport backpressure.
        var pending = new List<OutboundWork>(32);
        OutboundWork? continuation = null;
        try
        {
            while (continuation is not null || await _writes
                       .Reader.WaitToReadAsync()
                       .ConfigureAwait(false))
            {
                long bytes = 0;
                var operations = 0;
                OutboundWork? inputFlush = null;
                while (true)
                {
                    OutboundWork work;
                    if (continuation is { } group)
                    {
                        work = group;
                        continuation = null;
                    }
                    else
                    {
                        if (!_writes.Reader.TryRead(out var queued))
                        {
                            break;
                        }
                        work = queued;
                    }
                    if (!work.TryStart())
                    {
                        work.Batch.RemoveWrite(work);
                        continue;
                    }
                    try
                    {
                        lock (_gate)
                        {
                            ThrowIfStopped();
                        }
                        if (work.IsSync)
                        {
                            FrontendMessage
                                .Sync()
                                .Write(_output);
                            work.Batch.SealPublished();
                            bytes += 5;
                            operations++;
                        }
                        else if (work.IsQueryGroup)
                        {
                            var complete = true;
                            try
                            {
                                if (!work.HasSync || !work.Batch.RequestToken.IsCancellationRequested)
                                {
                                    complete = WriteQueryGroup(work, ref bytes, ref operations);
                                }
                            }
                            catch (OperationCanceledException) when (work.HasSync &&
                                                                     work.Batch.RequestToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
                            {
                                /* The encoder has stopped using inputs; its Sync still follows published queries. */
                            }
                            if (!complete || work.HasSync && (bytes >= 65536 || operations >= 256))
                            {
                                if (complete && work.HasSync)
                                {
                                    work.PrepareInputFlush();
                                    inputFlush = work;
                                }
                                work.PauseQueryGroup();
                                continuation = work;
                                break;
                            }
                            if (work.HasSync)
                            {
                                FrontendMessage
                                    .Sync()
                                    .Write(_output);
                                work.Batch.SealPublished();
                                bytes += 5;
                                operations++;
                            }
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
                                if (work.ResponseBatch != work.Batch)
                                {
                                    work.ResponseBatch.SyncGroup.RegisterPublished(work.Batch);
                                }
                                _output.Advance(work.Size);
                            }
                            bytes += work.Size;
                            operations++;
                        }
                        work.Published();
                        pending.Add(work);
                    }
                    catch (OperationCanceledException error) when (!work.IsSync && work.Batch.RequestToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
                    {
                        work.Complete(error);
                        work.Batch.RemoveWrite(work);
                    }
                    catch (Exception error)
                    {
                        work.Complete(Fail(error));
                        work.Batch.RemoveWrite(work);
                    }
                    if (bytes >= 65536 || operations >= 256)
                    {
                        break;
                    }
                }
                if (pending.Count == 0 && bytes == 0)
                {
                    continue;
                }
                try
                {
                    var flush = await _output
                        .FlushAsync(_lifetime.Token)
                        .ConfigureAwait(false);
                    if (flush.IsCanceled)
                    {
                        throw new OperationCanceledException(_lifetime.Token);
                    }
                    if (flush.IsCompleted)
                    {
                        throw new IOException("The output transport stopped reading.");
                    }
                    inputFlush?.InputsFlushed();
                    foreach (var work in pending)
                    {
                        work.Complete();
                    }
                }
                catch (Exception error)
                {
                    var failure = Fail(error);
                    foreach (var work in pending)
                    {
                        work.Complete(failure);
                    }
                }
                finally
                {
                    foreach (var work in pending)
                    {
                        work.Batch.RemoveWrite(work);
                    }
                    pending.Clear();
                }
            }
        }
        catch (Exception error)
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                Fail(error);
            }
        }
        finally
        {
            if (continuation is { } remaining)
            {
                remaining.Complete(_failure ?? new ObjectDisposedException(nameof(MpgsqlMessageSession)));
                remaining.Batch.RemoveWrite(remaining);
            }
            await _output
                .CompleteAsync(_failure)
                .ConfigureAwait(false);
        }
    }

    private bool WriteQueryGroup(
        OutboundWork work, ref long bytes,
        ref int operations
    )
    {
        while (work.TryGetQuery(out var query))
        {
            work.Batch.RequestToken.ThrowIfCancellationRequested();
            if (query.PreparedStatement is { } statement)
            {
                QueryPacket.WritePreparedMeasured(statement.Name, query.Parameters.Span, _output.GetSpan(query.EncodedSize), query.EncodedSize);
            }
            else
            {
                QueryPacket.WriteMeasured(query.Sql, query.Parameters.Span, _output.GetSpan(query.EncodedSize), query.EncodedSize);
            }
            work.Batch.RequestToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                ThrowIfStopped();
                work.Batch.RegisterOperation(query.PreparedStatement is null ? MessageOperationKind.Query : MessageOperationKind.PreparedQuery, query.PreparedStatement);
                _output.Advance(query.EncodedSize);
            }
            work.QueryPublished();
            bytes += query.EncodedSize;
            operations++;
            if (bytes >= 65536 || operations >= 256)
            {
                return !work.TryGetQuery(out _);
            }
        }
        return true;
    }

    private async Task ControlLoopAsync()
    {
        await foreach (var batch in _cancellations
                           .Reader.ReadAllAsync()
                           .ConfigureAwait(false))
        {
            batch.ProcessCancellation();
        }
    }

    private async Task ReceiveLoopAsync()
    {
        Exception? failure = null;
        try
        {
            MpgsqlQueryBatch? batch = null;
            while (true)
            {
                var read = await _input
                    .ReadAsync(_lifetime.Token)
                    .ConfigureAwait(false);
                var input = read.Buffer;
                try
                {
                    if (read.IsCanceled)
                    {
                        throw new OperationCanceledException(_lifetime.Token);
                    }
                    while (!input.IsEmpty)
                    {
                        // Only this loop removes response heads. Retain the active group across
                        // frames and refresh it at ReadyForQuery or when an idle read receives SQL.
                        if (batch is null)
                        {
                            lock (_gate)
                            {
                                batch = _responses.TryPeek(out var head) ? head : null;
                            }
                        }
                        if (!_frames.TryRead
                            (
                                ref input,
                                batch?.RowOwner.DiscardsRows == true,
                                out var message,
                                out var owner,
                                out var skippedColumns
                            ))
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
                            if (message.Kind == BackendMessageKind.ErrorResponse)
                            {
                                DiagnosticMessage diagnostics;
                                // Serialize receipt with write-side failure; fields own their decoded memory.
                                lock (_gate)
                                {
                                    _unrecoveredError = diagnostics = message.GetDiagnostics();
                                }
                                var terminal = (diagnostics.InvariantSeverity ?? diagnostics.Severity) is "FATAL" or "PANIC";
                                batch?.AcceptError(diagnostics, terminal);
                                if (batch is null || terminal)
                                {
                                    throw new MpgsqlServerException(diagnostics, null, null);
                                }
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
                                if (message.Kind == BackendMessageKind.DataRow && _rowBudget is { } budget)
                                {
                                    // Earlier completed groups may own all row capacity. Wake them
                                    // before waiting; the active group's budget waiter wakes itself.
                                    if (_completedNotificationCount != 0 && budget.WouldBlock(message.Payload.Length))
                                    {
                                        NotifyCompletedGroups();
                                    }
                                    if (await budget
                                            .ReserveAsync(message.Payload.Length, batch.RowOwner, _lifetime.Token)
                                            .ConfigureAwait(false))
                                    {
                                        reservation = budget;
                                    }
                                }
                                if (message.Kind == BackendMessageKind.ReadyForQuery)
                                {
                                    lock (_gate)
                                    {
                                        _transactionStatus = message.GetTransactionStatus();
                                    }
                                }
                                batch.Accept(message, ref owner, ref reservation, false);
                            }
                            finally { reservation?.Release(message.Payload.Length); }
                            if (message.Kind == BackendMessageKind.ReadyForQuery)
                            {
                                var completed = batch;
                                lock (_gate)
                                {
                                    _unrecoveredError = null;
                                    _responses.Dequeue();
                                    batch = _responses.TryPeek(out var head) ? head : null;
                                }
                                ReleaseBatch(completed);
                                completed.ReleaseSyncMembers();
                                if (!completed.IsSharedBoundary)
                                {
                                    _completedNotifications[_completedNotificationCount++] = completed;
                                    if (_completedNotificationCount == _completedNotifications.Length)
                                    {
                                        NotifyCompletedGroups();
                                    }
                                }
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
                    try { _input.AdvanceTo(input.Start, read.Buffer.End); }
                    finally { NotifyCompletedGroups(); }
                    // No timer or additional transport read: wake once all currently available
                    // frames are decoded. A full row budget wakes earlier, before waiting.
                    batch?.NotifyEvents();
                }
            }
        }
        catch (Exception error)
        {
            failure = error;
            if (Volatile.Read(ref _disposed) == 0)
            {
                failure = Fail(error);
            }
        }
        finally
        {
            _frames.Dispose();
            _cancellations.Writer.TryComplete();
            await _input
                .CompleteAsync(Volatile.Read(ref _disposed) == 0 ? failure : null)
                .ConfigureAwait(false);
        }
    }

    private void NotifyCompletedGroups()
    {
        for (var i = 0;
             i < _completedNotificationCount;
             i++)
        {
            var completed = _completedNotifications[i]!;
            _completedNotifications[i] = null;
            completed.NotifyEvents();
        }
        _completedNotificationCount = 0;
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
            ExceptionDispatchInfo.Throw(failure);
        }
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(MpgsqlMessageSession));
        }
        _lifetime.Token.ThrowIfCancellationRequested();
    }

    private Exception Fail(Exception error)
    {
        MpgsqlQueryBatch[] batches;
        MpgsqlPreparedStatement[] statements;
        lock (_gate)
        {
            if (_failure is not null)
            {
                return _failure;
            }
            if (Volatile.Read(ref _disposed) != 0)
            {
                return error;
            }
            // A completed head has already received ReadyForQuery, even if the reader has not
            // yet removed it from the FIFO. Do not reuse recovered diagnostics for a later failure.
            if (error is not MpgsqlServerException && _unrecoveredError is { } diagnostics
                                                   && (!_responses.TryPeek(out var head) || !head.Completion.IsCompleted))
            {
                error = new MpgsqlServerException(diagnostics, null, null, error);
            }
            _failure = error;
            batches = [.. _batches.Where(batch => !batch.Completion.IsCompleted)];
            statements = [.. _statements.Values, .. _localStatements.Select(pair => pair.Key)];
            _statements.Clear();
            _localStatements.Clear();
            _writes.Writer.TryComplete();
        }
        foreach (var statement in statements)
        {
            statement.FailPreparation(error);
        }
        foreach (var batch in batches)
        {
            batch.Fail(error);
            ReleaseBatch(batch);
        }
        _completion.TrySetException(error);
        _lifetime.Cancel();
        return error;
    }
}