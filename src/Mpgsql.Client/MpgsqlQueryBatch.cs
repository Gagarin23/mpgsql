using System.Buffers;
using Mpgsql.Internal;
using Mpgsql.Protocol;

namespace Mpgsql;

/// <summary>One ordered group of queries with a common Sync, transaction and error boundary.</summary>
/// <remarks>Cancellation stops admission and consumption, not SQL or the shared transport.</remarks>
public sealed class MpgsqlQueryBatch : IAsyncDisposable
{
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

    private readonly object _gate = new();

    private readonly ResultEventBuffer _events = new();

    private BatchCompletionSignal _completion;
    private BatchCompletionSignal _sealed;
    private BatchCompletionSignal _firstPublished;
    private readonly CancellationTokenRegistration _registration;
    private readonly MpgsqlMessageSession _session;
    private readonly SharedSyncGroup? _syncGroup;
    // Upper commands and grouped batches normally have one atomic writer item.
    // Keep it inline; raw producers can still submit any number of pending writes.
    private OutboundWork? _pendingWrite;
    private HashSet<OutboundWork>? _pendingWrites;
    // The ordinary Query prefix needs only its first index and length. Administrative and
    // prepared operations remain in a lazy FIFO behind that prefix, with their statement owners.
    private Queue<PendingResponse>? _responses;
    private int _firstResponseIndex;
    private int _plainResponseCount;
    private int _queryCount;
    private int _columnCount;
    private bool _hasRows;
    private bool _isSealed;
    private Phase _phase;
    private DiagnosticMessage? _error;
    private int? _errorIndex;
    private int _discard;
    private int _syncQueued;
    private int _readStarted;
    private int _disposed;
    private int _errorObserved;
    private MpgsqlResultReader? _reader;

    internal MpgsqlQueryBatch(MpgsqlMessageSession session,
        CancellationToken token, bool sharedSync = false)
    {
        _session = session;
        if (sharedSync) _syncGroup = new(this);
        RequestToken = token;
        _registration = token.UnsafeRegister(static state =>
            {
                var batch = (MpgsqlQueryBatch)state!;
                Volatile.Write(ref batch._discard,
                    1);
                batch._session.WakeRowBudget();
                batch._session.ScheduleDiscard(batch);
            },
            this);
    }

    internal CancellationToken RequestToken { get; }
    internal bool DiscardsRows => Volatile.Read(ref _discard) != 0;
    internal SharedSyncGroup SyncGroup => _syncGroup ?? throw new InvalidOperationException("Not a shared Sync boundary.");
    internal MpgsqlQueryBatch RowOwner => _syncGroup?.ActiveBatch ?? this;
    internal bool IsSharedBoundary => _syncGroup is not null;
    internal int SyncGroupIndex { get; set; }
    internal bool SyncGroupCommandCompleted { get; set; }
    internal void ReleaseSyncMembers() => _syncGroup?.ReleaseMembers(_session);

    internal int QueryCount
    {
        get
        {
            lock (_gate) return _queryCount;
        }
    }

    internal bool ConsumerDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Completes when the explicit Sync is published, independently of request cancellation.</summary>
    public Task Sealed { get { lock (_gate) return _sealed.Task; } }
    /// <summary>Completes at ReadyForQuery; SQL/transport errors fault this task even when consumption was cancelled.</summary>
    public Task Completion { get { lock (_gate) return _completion.Task; } }
    internal Task<bool> FirstPublished { get { lock (_gate) return _firstPublished.Task; } }
    public TransactionStatus? TransactionStatus { get; private set; }

    public ValueTask SendQueryAsync(string sql,
        ReadOnlyMemory<MpgsqlParameter> parameters = default)
    {
        ThrowForSend();
        return _session.SendQueryAsync(this,
            sql,
            parameters);
    }

    internal Task SendQueriesAsync(QueryDefinition[] queries) => _session.SendQueriesAsync(this, queries);
    internal OutboundWork SendExecution(QueryDefinition single, QueryDefinition[]? queries)
        => _session.SendExecution(this, single, queries);
    internal ValueTask SendQueryAsync(QueryDefinition query) => _session.SendQueryAsync(this, query);

    /// <summary>Queues named Parse without Sync. Await statement.Prepared only after sending Sync.</summary>
    public ValueTask SendPrepareAsync(MpgsqlPreparedStatement statement)
    {
        ThrowForSend();
        return _session.SendPrepareAsync(this, statement);
    }

    /// <summary>Queues binary Bind, portal Describe and unlimited Execute without another Parse or Sync.</summary>
    public ValueTask SendQueryAsync(MpgsqlPreparedStatement statement,
        ReadOnlyMemory<MpgsqlParameter> parameters = default)
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
    public ValueTask SendSyncAsync() => _session.SendSyncAsync(this);

    public async ValueTask<MpgsqlResultReader> ReadResultsAsync()
    {
        if (Interlocked.CompareExchange(ref _readStarted,
                1,
                0) != 0)
        {
            throw new InvalidOperationException("ReadResultsAsync can be called only once per group.");
        }
        if (ConsumerDisposed)
        {
            throw new ObjectDisposedException(nameof(MpgsqlQueryBatch));
        }
        try
        {
            RequestToken.ThrowIfCancellationRequested();
            _reader = new(this);
            await _reader.InitializeAsync().ConfigureAwait(false);
            return _reader;
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
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
                ? _queryCount++ : null;
            if (kind == MessageOperationKind.Query && (_responses?.Count ?? 0) == 0)
            {
                if (_plainResponseCount == 0) _firstResponseIndex = queryIndex!.Value;
                _plainResponseCount++;
            }
            else (_responses ??= new()).Enqueue(new(kind, statement, queryIndex));
            _firstPublished.TrySetResult(true);
            if (_phase == Phase.Idle)
                StartResponse();
        }
    }

    internal void SealPublished()
    {
        lock (_gate)
        {
            _isSealed = true;
            _firstPublished.TrySetResult(false);
            _sealed.TrySetResult();
            _syncGroup?.SealPublished();
        }
    }

    internal void SyncQueued()
    {
        if (Interlocked.CompareExchange(ref _syncQueued,
                1,
                0) != 0)
        {
            throw new InvalidOperationException("SendSyncAsync can be called only once per group.");
        }
    }

    internal void AddWrite(OutboundWork work)
    {
        lock (_gate)
        {
            if (_pendingWrite is null) _pendingWrite = work;
            else if (!ReferenceEquals(_pendingWrite, work)) (_pendingWrites ??= []).Add(work);
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
            if (ReferenceEquals(_pendingWrite, work)) _pendingWrite = null;
            else _pendingWrites?.Remove(work);
        }
    }
    internal void ReleaseRegistration() => _registration.Unregister();

    // Upper admission can be cancelled before this group ever owns a wire boundary.
    internal void CompleteIfUnpublished()
    {
        lock (_gate)
        {
            if (_syncQueued != 0 || _pendingWrite is not null || _pendingWrites?.Count > 0
                || _queryCount != 0 || _syncGroup?.Count > 0) return;
            _firstPublished.TrySetResult(false);
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
                Unexpected(BackendMessageKind.ErrorResponse);
            _error = diagnostics;
            _errorIndex = TryPeekResponse(out var failed) ? failed.QueryIndex : null;
            _phase = Phase.Recovery;
            DrainEvents();
        }
    }

    internal void Accept(BackendMessage message,
        ref IMemoryOwner<byte>? owner,
        ref RowBufferBudget? reservation, bool notifyEnd = true)
    {
        lock (_gate)
        {
            if (_syncGroup is { } group)
            {
                if (message.Kind == BackendMessageKind.ReadyForQuery)
                {
                    if (!_isSealed) Unexpected(message.Kind);
                    TransactionStatus = message.GetTransactionStatus();
                    group.Ready(TransactionStatus.Value);
                    Complete(null); // SQL errors were attributed to the logical requests.
                }
                else group.Accept(message, ref owner, ref reservation);
                return;
            }
            if (message.Kind == BackendMessageKind.ReadyForQuery)
            {
                if (!_isSealed || (_phase != Phase.Recovery &&
                                   (_phase != Phase.Idle || HasResponses)))
                {
                    Unexpected(message.Kind);
                }
                TransactionStatus = message.GetTransactionStatus();
                var failure = _error is { } error
                    ? new MpgsqlServerException(error,
                        _errorIndex,
                        TransactionStatus.Value)
                    : null;
                if (failure is not null)
                    FailPreparations(failure);
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
                        _phase = Phase.Bind;
                    break;
                case (Phase.Close, BackendMessageKind.CloseComplete):
                    response.Statement!.ConfirmClosed();
                    EndResponse();
                    break;
                case (Phase.Bind, BackendMessageKind.BindComplete): _phase = Phase.Describe; break;
                case (Phase.Describe, BackendMessageKind.RowDescription):
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
                        if (column.Format != FormatCode.Binary)
                        {
                            throw new InvalidDataException("A binary result has a text column.");
                        }
                    _columnCount = columns.Length;
                    _hasRows = true;
                    _phase = Phase.Rows;
                    Publish(new(response.QueryIndex!.Value,
                        columns, IsRowSet: true));
                    break;
                case (Phase.Describe, BackendMessageKind.NoData):
                    _columnCount = 0;
                    _hasRows = false;
                    _phase = Phase.Rows;
                    Publish(new(response.QueryIndex!.Value,
                        default));
                    break;
                case (Phase.Rows, BackendMessageKind.DataRow):
                    ValidateRow(message.GetDataRow().Count);
                    if (!DiscardsRows)
                    {
                        var row = _session.OwnRow(message, owner, reservation);
                        reservation = null;
                        if (owner is null)
                        {
                            _session.RecordRowCopy(message.Payload.Length);
                        }
                        owner = null;
                        Publish(new(response.QueryIndex!.Value,
                            default,
                            row));
                    }
                    else OwnedRow.ValidateValues(message);
                    break;
                case (Phase.Rows, BackendMessageKind.CommandComplete):
                case (Phase.Rows, BackendMessageKind.EmptyQueryResponse):
                    Publish(new(response.QueryIndex!.Value,
                        default,
                        CommandTag:
                        !DiscardsRows && message.Kind == BackendMessageKind.CommandComplete
                            ? _session.DecodeCommandTag(message)
                            : null,
                        IsEnd: true), notifyEnd);
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
                MessageOperationKind.PreparedQuery => Phase.Bind,
                MessageOperationKind.Close => Phase.Close,
                _ => throw new InvalidDataException("Sync does not have an operation response.")
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
        else _responses!.Dequeue();
        StartResponse();
    }

    private bool HasResponses => _plainResponseCount != 0 || (_responses?.Count ?? 0) != 0;

    private bool TryPeekResponse(out PendingResponse response)
    {
        if (_plainResponseCount != 0)
        {
            response = new(MessageOperationKind.Query, null, _firstResponseIndex);
            return true;
        }
        if (_responses is not null) return _responses.TryPeek(out response);
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
        if (_responses is null) return;
        foreach (var response in _responses)
            if (response.Kind == MessageOperationKind.Prepare)
                response.Statement!.FailPreparation(error);
    }

    internal void AcceptSkippedRow(int columns)
    {
        if (_syncGroup is { ActiveBatch: { } active }) active.AcceptSkippedRow(columns);
        else lock (_gate) ValidateRow(columns);
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
        if (DiscardsRows || !_events.TryWrite(result, notify: notifyEnd))
        {
            result.Row?.Dispose();
        }
    }

    internal void NotifyEvents()
    {
        if (_syncGroup is { } group) group.ActiveBatch?.NotifyEvents();
        else _events.NotifyAvailable();
    }

    internal void CompleteShared(TransactionStatus status, Exception? error)
    {
        lock (_gate)
        {
            if (!_isSealed || (error is null && (_phase != Phase.Idle || HasResponses)))
                Unexpected(BackendMessageKind.ReadyForQuery);
            TransactionStatus = status;
            if (error is not null) DrainEvents();
            ClearResponses();
            Complete(error);
        }
    }

    private void Complete(Exception? error, bool notify = true)
    {
        // A physical shared boundary has no consumer and must not remain registered after RFQ.
        if (_syncGroup is not null) Volatile.Write(ref _disposed, 1);
        if (error is null)
        {
            _completion.TrySetResult();
        }
        else
        {
            _completion.TrySetException(error);
        }
        _events.Complete(error, notify);
    }

    internal void Fail(Exception error)
    {
        lock (_gate)
        {
            if (_error is { } diagnostics && TransactionStatus is null)
            {
                var terminal = error as MpgsqlServerException;
                // A later connection diagnostic supersedes the group's earlier SQL error.
                error = new MpgsqlServerException(terminal?.Diagnostics ?? diagnostics, _errorIndex, null,
                    terminal is null ? error : terminal.InnerException);
            }
            Volatile.Write(ref _discard,
                1);
            _syncGroup?.Fail(error);
            _pendingWrite?.FailQueued(error);
            if (_pendingWrites is { } writes)
                foreach (var work in writes) work.FailQueued(error);
            FailPreparations(error);
            DrainEvents();
            _sealed.TrySetException(error);
            _firstPublished.TrySetResult(false);
            Complete(error);
        }
    }

    internal void ProcessCancellation()
    {
        lock (_gate)
        {
            _pendingWrite?.Cancel();
            if (_pendingWrites is { } writes)
                foreach (var work in writes) work.Cancel();
            DrainEvents();
            _events.Complete(new OperationCanceledException(RequestToken));
        }
    }

    private void DrainEvents()
    {
        _events.Drain();
    }

    internal bool TryReadEvent(out ResultEvent result) => _events.TryRead(out result);
    internal ValueTask<bool> WaitForEventAsync() => _events.WaitToReadAsync();

    internal async ValueTask ObserveCompletionAsync()
    {
        try { await Completion.ConfigureAwait(false); }
        catch
        {
            if (Interlocked.Exchange(ref _errorObserved,
                    1) == 0)
            {
                throw;
            }
        }
    }

    internal void BeginDiscard()
    {
        Volatile.Write(ref _discard,
            1);
        lock (_gate)
        {
            DrainEvents();
            _events.Complete();
        }
        _session.WakeRowBudget();
    }

    internal async ValueTask DiscardResultsAsync()
    {
        BeginDiscard();
        _reader?.ReleaseCurrent();
        if (Volatile.Read(ref _syncQueued) != 0 && !RequestToken.IsCancellationRequested)
        {
            await ObserveCompletionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Discards consumption. Never sends Sync; the sending flow must still call SendSyncAsync.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed,
                1) != 0)
        {
            return;
        }
        try { await DiscardResultsAsync().ConfigureAwait(false); }
        finally { _session.ReleaseBatch(this); }
    }

    private void Unexpected(BackendMessageKind kind)
        => throw new InvalidDataException($"Unexpected {kind} in phase {_phase}.");
}
