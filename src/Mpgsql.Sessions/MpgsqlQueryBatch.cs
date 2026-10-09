using System.Buffers;
using System.Runtime.CompilerServices;
using Mpgsql.Internal;
using Mpgsql.Protocol;

namespace Mpgsql;

/// <summary>One ordered group of queries with a common Sync, transaction and error boundary.</summary>
/// <remarks>Cancellation stops admission and consumption, not SQL or the shared transport.</remarks>
public sealed class MpgsqlQueryBatch : IAsyncDisposable, IQueryGroup
{

    private readonly ResultEventBuffer? _events;

    private readonly object _gate = new object();
    private readonly CancellationTokenRegistration _registration;
    private readonly MpgsqlMessageSession _session;
    private readonly SharedSyncGroup? _syncGroup;
    private ResultEvent? _adoEvent;
    private volatile bool _adoConsumptionCancelled;
    private long _affectedRows = -1;
    private long[]? _affectedRowsByQuery;
    private bool _collectAffectedRows;
    private int _columnCount;
    private int _currentRowIndex;

    private BatchCompletionSignal _completion;
    private int _discard;
    private int _disposed;
    private DiagnosticMessage? _error;
    private int? _errorIndex;
    private int _errorObserved;
    private BatchCompletionSignal _firstPublished;
    private int _firstResponseIndex;
    private bool _hasRows;

    private bool _isSealed;

    // Upper commands and grouped batches normally have one atomic writer item.
    // Keep it inline; raw producers can still submit any number of pending writes.
    private OutboundWork? _pendingWrite;
    private HashSet<OutboundWork>? _pendingWrites;
    private Phase _phase;
    private int _plainResponseCount;
    private int _queryCount;
    private int _readStarted;

    private MpgsqlResultReader? _reader;

    // The ordinary Query prefix needs only its first index and length. Administrative and
    // prepared operations remain in a lazy FIFO behind that prefix, with their statement owners.
    private Queue<PendingResponse>? _responses;
    private BatchCompletionSignal _sealed;
    private int _syncQueued;

    internal MpgsqlQueryBatch(
        MpgsqlMessageSession session,
        CancellationToken token, bool sharedSync = false
    )
    {
        _session = session;
        if (!session.IsAdoSession)
            _events = new ResultEventBuffer();
        if (sharedSync)
        {
            _syncGroup = new SharedSyncGroup(this);
        }
        RequestToken = token;
        _registration = token.UnsafeRegister
        (
            static state =>
            {
                var batch = (MpgsqlQueryBatch)state!;
                Volatile.Write
                (
                    ref batch._discard,
                    1
                );
                batch._session.WakeRowBudget();
                batch._session.ScheduleDiscard(batch);
            },
            this
        );
    }
    internal TimeSpan RecoveryTimeout { get; set; }
    // The work's lazy physical delivery uses this existing gate only until its
    // private promise is materialized or a completed marker is published.
    internal object DeliveryGate => _gate;
    CancellationToken IQueryGroup.RequestToken => RequestToken;
    bool IQueryGroup.CancellationRequested => CancellationRequested;
    bool IQueryGroup.IsAdoSession => IsAdoSession;
    object IQueryGroup.DeliveryGate => DeliveryGate;
    void IQueryGroup.ThrowForSend() => ThrowForSend();
    void IQueryGroup.SyncQueued() => SyncQueued();
    void IQueryGroup.SealPublished() => SealPublished();
    void IQueryGroup.RegisterOperation(MessageOperationKind kind, MpgsqlPreparedStatement? statement) => RegisterOperation(kind, statement);
    void IQueryGroup.AddWrite(OutboundWork work) => AddWrite(work);
    void IQueryGroup.RemoveWrite(OutboundWork work) => RemoveWrite(work);
    internal long RecordsAffected => Volatile.Read(ref _affectedRows);

    internal CancellationToken RequestToken { get; }
    internal bool CancellationRequested => _adoConsumptionCancelled || RequestToken.IsCancellationRequested;
    internal bool DiscardsRows => Volatile.Read(ref _discard) != 0;
    internal bool IsAdoSession => _session.IsAdoSession;
    internal bool AdoConsumptionStopped => DiscardsRows || _adoConsumptionCancelled;
    internal bool AdoReaderDisposed => _reader?.IsDisposed == true;
    internal BorrowedRow AdoRow => _session.AdoRow;
    internal void ReleaseAdoRow() => _session.ReleaseAdoRow();
    internal SharedSyncGroup SyncGroup => _syncGroup ?? throw new InvalidOperationException("Not a shared Sync boundary.");
    internal MpgsqlQueryBatch RowOwner => _syncGroup?.ActiveBatch ?? this;
    internal bool IsSharedBoundary => _syncGroup is not null;
    internal int SyncGroupIndex { get; set; }
    internal bool SyncGroupCommandCompleted { get; set; }

    internal int QueryCount
    {
        get
        {
            lock (_gate)
            {
                return _queryCount;
            }
        }
    }

    internal bool ConsumerDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Completes when the explicit Sync is published, independently of request cancellation.</summary>
    public Task Sealed
    {
        get
        {
            lock (_gate)
            {
                return _sealed.Task;
            }
        }
    }

    /// <summary>Completes at ReadyForQuery; SQL/transport errors fault this task even when consumption was cancelled.</summary>
    public Task Completion
    {
        get
        {
            lock (_gate)
            {
                return _completion.Task;
            }
        }
    }

    // Status polling must not materialize a completion promise before RFQ.
    // The same gate publishes successful and failed protocol completion.
    internal bool ProtocolCompleted
    {
        get
        {
            lock (_gate)
                return _completion.IsCompleted;
        }
    }

    internal Task<bool> FirstPublished
    {
        get
        {
            lock (_gate)
            {
                return _firstPublished.Task;
            }
        }
    }

    public TransactionStatus? TransactionStatus { get; private set; }

    private bool HasResponses => _plainResponseCount != 0 || (_responses?.Count ?? 0) != 0;

    /// <summary>Discards consumption. Never sends Sync; the sending flow must still call SendSyncAsync.</summary>
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
        try
        {
            await DiscardResultsAsync()
                .ConfigureAwait(false);
        }
        finally { _session.ReleaseBatch(this); }
    }

    // Optional ADO.NET bookkeeping at CommandComplete, including discarded results.
    // It never calls upper-layer/user code and does not change the per-row path.
    internal void CollectAffectedRows(long[]? perQuery)
    {
        _collectAffectedRows = true;
        _affectedRowsByQuery = perQuery;
    }
    internal void ReleaseSyncMembers()
    {
        _syncGroup?.ReleaseMembers(_session);
    }

    public ValueTask SendQueryAsync(
        string sql,
        ReadOnlyMemory<MpgsqlParameterValue> parameters = default
    )
    {
        ThrowForSend();
        return _session.SendQueryAsync
        (
            this,
            sql,
            parameters
        );
    }

    internal Task SendQueriesAsync(QueryDefinition[] queries)
    {
        return _session.SendQueriesAsync(this, queries);
    }
    internal OutboundWork SendExecution(QueryDefinition single, QueryDefinition[]? queries)
    {
        return _session.SendExecution(this, single, queries);
    }
    internal ValueTask SendQueryAsync(QueryDefinition query)
    {
        return _session.SendQueryAsync(this, query);
    }

    /// <summary>Queues named Parse without Sync. Await statement.Prepared only after sending Sync.</summary>
    public ValueTask SendPrepareAsync(MpgsqlPreparedStatement statement)
    {
        ThrowForSend();
        return _session.SendPrepareAsync(this, statement);
    }

    /// <summary>Queues binary Bind, portal Describe and unlimited Execute without another Parse or Sync.</summary>
    public ValueTask SendQueryAsync(
        MpgsqlPreparedStatement statement,
        ReadOnlyMemory<MpgsqlParameterValue> parameters = default
    )
    {
        ThrowForSend();
        return _session.SendQueryAsync(this, statement, parameters);
    }

    /// <summary>Queues Close without Sync and prevents further executions of this statement.</summary>
    /// <remarks>Await Completion for acknowledgement. Retry a skipped Close in a new batch after recovery.</remarks>
    public ValueTask SendCloseAsync(MpgsqlPreparedStatement statement)
    {
        ThrowForSend();
        return _session.SendCloseAsync(this, statement);
    }

    /// <summary>Queues exactly one Sync after all previously submitted sends, including sends not yet awaited.</summary>
    /// <remarks>Call from the sending flow. Request cancellation never suppresses this recovery boundary.</remarks>
    public ValueTask SendSyncAsync()
    {
        return _session.SendSyncAsync(this);
    }

    public ValueTask<MpgsqlResultReader> ReadResultsAsync() => ReadResultsAsync(null);

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    internal async ValueTask<MpgsqlResultReader> ReadResultsAsync(IResultExecutionOwner? execution)
    {
        ClaimReader();
        try
        {
            var reader = RegisterReader(execution);
            await reader
                .InitializeAsync()
                .ConfigureAwait(false);
            return reader;
        }
        catch
        {
            if (IsAdoSession)
                BeginDiscard(); // The execution owner serializes the ADO recovery drain.
            else
                await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // Exclusive ADO owners await initialization themselves, without a second
    // asynchronous wrapper. Registration must precede the first input movement.
    internal MpgsqlResultReader CreateAdoReader(IResultExecutionOwner execution)
    {
        ClaimReader();
        try { return RegisterReader(execution); }
        catch { BeginDiscard(); throw; }
    }

    private void ClaimReader()
    {
        if (Interlocked.CompareExchange
            (
                ref _readStarted,
                1,
                0
            ) != 0)
        {
            throw new InvalidOperationException("ReadResultsAsync can be called only once per group.");
        }
        if (ConsumerDisposed)
        {
            throw new ObjectDisposedException(nameof(MpgsqlQueryBatch));
        }
    }

    private MpgsqlResultReader RegisterReader(IResultExecutionOwner? execution)
    {
        RequestToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (IsAdoSession && AdoConsumptionStopped)
            {
                // Fail stops consumption too. Preserve its already published
                // terminal diagnostic instead of reporting owner disposal; this
                // completed task cannot wait while the batch gate is held.
                if (_completion.IsCompleted)
                    _completion.Task.GetAwaiter().GetResult();
                if (DiscardsRows && !RequestToken.IsCancellationRequested)
                    throw new ObjectDisposedException(nameof(MpgsqlResultReader));
                throw new OperationCanceledException(RequestToken);
            }
            _reader = new MpgsqlResultReader(this);
            _reader.Execution = execution;
            return _reader;
        }
    }

    internal void ThrowForSend()
    {
        RequestToken.ThrowIfCancellationRequested();
        if (ConsumerDisposed)
        {
            throw new ObjectDisposedException(nameof(MpgsqlQueryBatch));
        }
        if (Volatile.Read(ref _syncQueued) != 0)
        {
            throw new InvalidOperationException("The query group is already closing.");
        }
    }

    internal void RegisterOperation(MessageOperationKind kind, MpgsqlPreparedStatement? statement)
    {
        lock (_gate)
        {
            int? queryIndex = kind is MessageOperationKind.Query or MessageOperationKind.PreparedQuery
                ? _queryCount++
                : null;
            if (kind == MessageOperationKind.Query && (_responses?.Count ?? 0) == 0)
            {
                if (_plainResponseCount == 0)
                {
                    _firstResponseIndex = queryIndex!.Value;
                }
                _plainResponseCount++;
            }
            else
            {
                (_responses ??= new Queue<PendingResponse>()).Enqueue(new PendingResponse(kind, statement, queryIndex));
            }
            _firstPublished.TrySetResult(true);
            if (_phase == Phase.Idle)
            {
                StartResponse();
            }
        }
    }

    internal void SealPublished()
    {
        lock (_gate)
        {
            _isSealed = true;
            _firstPublished.TrySetResult();
            _sealed.TrySetResult();
            _syncGroup?.SealPublished();
        }
    }

    internal void SyncQueued()
    {
        if (Interlocked.CompareExchange
            (
                ref _syncQueued,
                1,
                0
            ) != 0)
        {
            throw new InvalidOperationException("SendSyncAsync can be called only once per group.");
        }
    }

    internal void AddWrite(OutboundWork work)
    {
        lock (_gate)
        {
            if (_pendingWrite is null)
            {
                _pendingWrite = work;
            }
            else if (!ReferenceEquals(_pendingWrite, work))
            {
                (_pendingWrites ??= []).Add(work);
            }
            if (RequestToken.IsCancellationRequested)
            {
                work.Cancel();
            }
        }
    }

    internal void RemoveWrite(OutboundWork work)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_pendingWrite, work))
            {
                _pendingWrite = null;
            }
            else
            {
                _pendingWrites?.Remove(work);
            }
        }
    }
    internal void ReleaseRegistration()
    {
        _registration.Unregister();
    }

    // Upper admission can be cancelled before this group ever owns a wire boundary.
    internal void CompleteIfUnpublished()
    {
        lock (_gate)
        {
            if (_syncQueued != 0 || _pendingWrite is not null || _pendingWrites?.Count > 0
                || _queryCount != 0 || _syncGroup?.Count > 0)
            {
                return;
            }
            _firstPublished.TrySetResult();
            _sealed.TrySetResult();
            Complete(null);
        }
        _session.ReleaseBatch(this);
    }

    // The session decodes each ErrorResponse once and also retains it for terminal failure.
    internal void AcceptError(DiagnosticMessage diagnostics, bool terminal)
    {
        lock (_gate)
        {
            _syncGroup?.AcceptError(diagnostics, terminal);
            if (_phase == Phase.Recovery && !terminal)
            {
                Unexpected(BackendMessageKind.ErrorResponse);
            }
            _error = diagnostics;
            _errorIndex = TryPeekResponse(out var failed) ? failed.QueryIndex : null;
            _phase = Phase.Recovery;
            DrainEvents();
        }
        if (!terminal && RecoveryTimeout > TimeSpan.Zero)
        {
            _ = BoundErrorRecoveryAsync();
        }
    }

    private async Task BoundErrorRecoveryAsync()
    {
        try
        {
            await Completion
                .WaitAsync(RecoveryTimeout)
                .ConfigureAwait(false);
        }
        catch (TimeoutException error) { _session.Abort(error); }
        catch { }
    }

    internal void Accept(
        BackendMessage message,
        ref IMemoryOwner<byte>? owner,
        ref RowBufferBudget? reservation, bool notifyEnd = true
    )
    {
        lock (_gate)
        {
            if (_syncGroup is { } group)
            {
                if (message.Kind == BackendMessageKind.ReadyForQuery)
                {
                    if (!_isSealed)
                    {
                        Unexpected(message.Kind);
                    }
                    TransactionStatus = message.GetTransactionStatus();
                    group.Ready(TransactionStatus.Value);
                    Complete(null); // SQL errors were attributed to the logical requests.
                }
                else
                {
                    group.Accept(message, ref owner, ref reservation);
                }
                return;
            }
            if (message.Kind == BackendMessageKind.ReadyForQuery)
            {
                if (!_isSealed || _phase != Phase.Recovery &&
                    (_phase != Phase.Idle || HasResponses))
                {
                    Unexpected(message.Kind);
                }
                TransactionStatus = message.GetTransactionStatus();
                var failure = _error is { } error
                    ? new MpgsqlServerException
                    (
                        error,
                        _errorIndex,
                        TransactionStatus.Value
                    )
                    : null;
                if (failure is not null)
                {
                    FailPreparations(failure);
                }
                ClearResponses();
                Complete(failure, notifyEnd);
                return;
            }
            if (!HasResponses || _phase == Phase.Recovery)
            {
                Unexpected(message.Kind);
            }
            _ = TryPeekResponse(out var response);
            switch (_phase, message.Kind)
            {
                case (Phase.Parse, BackendMessageKind.ParseComplete):
                    if (response.Kind == MessageOperationKind.Prepare)
                    {
                        response.Statement!.ConfirmPrepared();
                        EndResponse();
                    }
                    else
                    {
                        _phase = Phase.Bind;
                    }
                    break;
                case (Phase.Close, BackendMessageKind.CloseComplete):
                    response.Statement!.ConfirmClosed();
                    EndResponse();
                    break;
                case (Phase.Bind, BackendMessageKind.BindComplete): _phase = Phase.Describe; break;
                case (Phase.Describe, BackendMessageKind.RowDescription):
                    _currentRowIndex = response.QueryIndex!.Value;
                    if (DiscardsRows)
                    {
                        var description = new WireReader(message.Payload);
                        _columnCount = description.Count();
                        _hasRows = true;
                        _phase = Phase.Rows;
                        break;
                    }
                    var columns = _session.DecodeRowDescription(message);
                    foreach (var column in columns.Span)
                    {
                        if (column.Format != FormatCode.Binary)
                        {
                            throw new InvalidDataException("A binary result has a text column.");
                        }
                    }
                    _columnCount = columns.Length;
                    _hasRows = true;
                    _phase = Phase.Rows;
                    Publish
                    (
                        new ResultEvent
                        (
                            response.QueryIndex!.Value,
                            columns, IsRowSet: true
                        )
                    );
                    break;
                case (Phase.Describe, BackendMessageKind.NoData):
                    _currentRowIndex = response.QueryIndex!.Value;
                    _columnCount = 0;
                    _hasRows = false;
                    _phase = Phase.Rows;
                    Publish
                    (
                        new ResultEvent
                        (
                            response.QueryIndex!.Value,
                            default
                        )
                    );
                    break;
                case (Phase.Rows, BackendMessageKind.DataRow):
                    ValidateRow
                    (
                        message.GetDataRow()
                            .Count
                    );
                    if (!DiscardsRows)
                    {
                        var row = _session.OwnRow(message, owner, reservation);
                        reservation = null;
                        if (owner is null)
                        {
                            _session.RecordRowCopy(message.Payload.Length);
                        }
                        owner = null;
                        Publish
                        (
                            new ResultEvent
                            (
                                response.QueryIndex!.Value,
                                default,
                                row
                            )
                        );
                    }
                    else
                    {
                        OwnedRow.ValidateValues(message);
                    }
                    break;
                case (Phase.Rows, BackendMessageKind.CommandComplete):
                case (Phase.Rows, BackendMessageKind.EmptyQueryResponse):
                    var tag = message.Kind == BackendMessageKind.CommandComplete && (!DiscardsRows || _collectAffectedRows)
                        ? _session.DecodeCommandTag(message)
                        : null;
                    if (_collectAffectedRows)
                    {
                        var rows = ResultConsumption.AffectedRows(tag) ?? -1;
                        if (_affectedRowsByQuery is { } counts)
                        {
                            counts[response.QueryIndex!.Value] = rows;
                        }
                        if (rows >= 0)
                        {
                            Volatile.Write(ref _affectedRows, checked(Math.Max(0, _affectedRows) + rows));
                        }
                    }
                    Publish
                    (
                        new ResultEvent
                        (
                            response.QueryIndex!.Value,
                            default,
                            CommandTag: tag,
                            IsEnd: true
                        ), notifyEnd
                    );
                    EndResponse();
                    break;
                default: Unexpected(message.Kind); break;
            }
        }
    }

    private void StartResponse()
    {
        _phase = TryPeekResponse(out var response)
            ? response.Kind switch
            {
                MessageOperationKind.Query or MessageOperationKind.Prepare => Phase.Parse,
                MessageOperationKind.PreparedQuery                         => Phase.Bind,
                MessageOperationKind.Close                                 => Phase.Close,
                _                                                          => throw new InvalidDataException("Sync does not have an operation response.")
            }
            : Phase.Idle;
    }

    private void EndResponse()
    {
        if (_plainResponseCount != 0)
        {
            _plainResponseCount--;
            _firstResponseIndex++;
        }
        else
        {
            _responses!.Dequeue();
        }
        StartResponse();
    }

    private bool TryPeekResponse(out PendingResponse response)
    {
        if (_plainResponseCount != 0)
        {
            response = new PendingResponse(MessageOperationKind.Query, null, _firstResponseIndex);
            return true;
        }
        if (_responses is not null)
        {
            return _responses.TryPeek(out response);
        }
        response = default;
        return false;
    }

    private void ClearResponses()
    {
        _plainResponseCount = 0;
        _responses?.Clear();
    }

    private void FailPreparations(Exception error)
    {
        if (_responses is null)
        {
            return;
        }
        foreach (var response in _responses)
        {
            if (response.Kind == MessageOperationKind.Prepare)
            {
                response.Statement!.FailPreparation(error);
            }
        }
    }

    internal void AcceptSkippedRow(int columns)
    {
        if (_syncGroup is {ActiveBatch: { } active})
        {
            active.AcceptSkippedRow(columns);
        }
        else
        {
            lock (_gate)
            {
                ValidateRow(columns);
            }
        }
    }

    private void ValidateRow(int columns)
    {
        if (_phase != Phase.Rows || !_hasRows || _columnCount != columns)
        {
            throw new InvalidDataException("DataRow does not match the active portal description.");
        }
    }

    private void Publish(ResultEvent result, bool notifyEnd = false)
    {
        if (IsAdoSession)
        {
            if (!DiscardsRows)
                _adoEvent = result;
            return;
        }
        if (DiscardsRows || !_events!.TryWrite(result, notifyEnd))
        {
            result.Row?.Dispose();
        }
    }

    internal void NotifyEvents()
    {
        if (_syncGroup is { } group)
        {
            group.ActiveBatch?.NotifyEvents();
        }
        else
        {
            _events?.NotifyAvailable();
        }
    }

    internal void CompleteShared(TransactionStatus status, Exception? error)
    {
        lock (_gate)
        {
            if (!_isSealed || error is null && (_phase != Phase.Idle || HasResponses))
            {
                Unexpected(BackendMessageKind.ReadyForQuery);
            }
            TransactionStatus = status;
            if (error is not null)
            {
                DrainEvents();
            }
            ClearResponses();
            Complete(error);
        }
    }

    private void Complete(Exception? error, bool notify = true)
    {
        // A physical shared boundary has no consumer and must not remain registered after RFQ.
        if (_syncGroup is not null)
        {
            Volatile.Write(ref _disposed, 1);
        }
        if (error is null)
        {
            _completion.TrySetResult();
        }
        else
        {
            _completion.TrySetException(error);
        }
        _events?.Complete(error, notify);
    }

    internal void Fail(Exception error)
    {
        lock (_gate)
        {
            if (_error is { } diagnostics && TransactionStatus is null)
            {
                var terminal = error as MpgsqlServerException;
                // A later connection diagnostic supersedes the group's earlier SQL error.
                error = new MpgsqlServerException
                (
                    terminal?.Diagnostics ?? diagnostics, _errorIndex, null,
                    terminal is null ? error : terminal.InnerException
                );
            }
            Volatile.Write
            (
                ref _discard,
                1
            );
            _syncGroup?.Fail(error);
            _pendingWrite?.FailQueued(error);
            if (_pendingWrites is { } writes)
            {
                foreach (var work in writes)
                {
                    work.FailQueued(error);
                }
            }
            FailPreparations(error);
            DrainEvents();
            _sealed.TrySetException(error);
            _firstPublished.TrySetResult();
            Complete(error);
        }
    }

    internal void ProcessCancellation()
    {
        lock (_gate)
        {
            _adoConsumptionCancelled = true;
            _pendingWrite?.Cancel();
            if (_pendingWrites is { } writes)
            {
                foreach (var work in writes)
                {
                    work.Cancel();
                }
            }
            DrainEvents();
            _events?.Complete(new OperationCanceledException(RequestToken));
        }
    }

    private void DrainEvents()
    {
        _events?.Drain();
    }

    internal bool TryReadEvent(out ResultEvent result)
    {
        if (IsAdoSession)
            return _session.TryReadAdoEvent(this, out result);
        return _events!.TryRead(out result);
    }
    internal ValueTask<bool> WaitForEventAsync()
    {
        if (IsAdoSession)
            return _session.WaitForAdoInputAsync(this);
        return _events!.WaitToReadAsync();
    }

    internal bool TryTakeAdoEvent(out ResultEvent result)
    {
        if (_adoEvent is { } value)
        {
            _adoEvent = null;
            result = value;
            return true;
        }
        result = default;
        return false;
    }
    internal int AdoRowIndex => _currentRowIndex;
    internal bool TryReadAdoRow() => IsAdoSession && _adoEvent is null && _session.TryReadAdoRow(this);
    internal int AcceptBorrowedRow(int columns)
    {
        // Only the exclusive receive owner changes the protocol phase. Cancellation
        // changes consumption and pending admission, never the response cursor.
        ValidateRow(columns);
        return _currentRowIndex;
    }
    internal Task InvalidateReaderFromOwner()
    {
        lock (_gate)
            return _reader?.InvalidateFromOwner() ?? Task.CompletedTask;
    }

    internal async ValueTask ObserveCompletionAsync()
    {
        try { await Completion.ConfigureAwait(false); }
        catch
        {
            if (Interlocked.Exchange
                (
                    ref _errorObserved,
                    1
                ) == 0)
            {
                throw;
            }
        }
    }

    internal void BeginDiscard()
    {
        Volatile.Write
        (
            ref _discard,
            1
        );
        lock (_gate)
        {
            DrainEvents();
            _events?.Complete();
        }
        _session.WakeRowBudget();
    }

    internal async ValueTask DiscardResultsAsync()
    {
        BeginDiscard();
        _reader?.ReleaseCurrent();
        if (IsAdoSession && Volatile.Read(ref _syncQueued) != 0 && !Completion.IsCompleted)
            await _session.DrainAdoAsync(this).ConfigureAwait(false);
        if (Volatile.Read(ref _syncQueued) != 0 && !RequestToken.IsCancellationRequested)
        {
            await ObserveCompletionAsync()
                .ConfigureAwait(false);
        }
    }

    private void Unexpected(BackendMessageKind kind)
    {
        throw new InvalidDataException($"Unexpected {kind} in phase {_phase}.");
    }

    private enum Phase
    {
        Idle,
        Parse,
        Bind,
        Describe,
        Rows,
        Close,
        Recovery
    }
}
