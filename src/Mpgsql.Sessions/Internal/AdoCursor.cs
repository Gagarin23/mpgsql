using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Mpgsql.Converters;
using Mpgsql.Protocol;

namespace Mpgsql.Internal;

// One exclusively owned ADO operation. Protocol phase and current result are
// advanced together by its input owner, without a raw reader or result-event slot.
internal sealed class AdoCursor : IQueryGroup, IAsyncDisposable
{
    private const int ActiveMovement = 1;
    private const int Closed = 2;
    private readonly object _gate = new();
    private readonly MpgsqlMessageSession _session;
    private readonly IResultExecutionOwner _execution;
    private BatchCompletionSignal _completion;
    private BatchCompletionSignal _firstPublished;
    private OutboundWork? _pendingWrite;
    private HashSet<OutboundWork>? _pendingWrites;
    private Queue<PendingResponse>? _responses;
    private int _plainResponseCount;
    private int _firstResponseIndex;
    private int _queryCount;
    private Phase _phase;
    private bool _isSealed;
    private int _syncQueued;
    private DiagnosticMessage? _error;
    private int? _errorIndex;
    private int _errorObserved;
    private int _discard;
    private volatile bool _cancelled;
    private volatile bool _disposed;
    private int _movementState;
    private TaskCompletionSource? _idle;
    private bool _initialized;
    private bool _resultEnd;
    private bool _finished;
    private bool _borrowedRow;
    private int _columnCount;
    private bool _hasRows;
    private RowField[]? _columnArray;
    private int _columnArrayOffset;
    private long _affectedRows = -1;
    private long[]? _affectedRowsByQuery;
    private bool _collectAffectedRows;

    internal AdoCursor(MpgsqlMessageSession session, CancellationToken request, IResultExecutionOwner execution)
    {
        _session = session;
        RequestToken = request;
        _execution = execution;
    }

    internal CancellationToken RequestToken { get; }
    internal TimeSpan RecoveryTimeout { get; set; }
    internal bool CancellationRequested => _cancelled || RequestToken.IsCancellationRequested;
    internal bool DiscardsRows => Volatile.Read(ref _discard) != 0;
    internal bool ConsumptionStopped => DiscardsRows || _cancelled;
    internal bool IsDisposed => _disposed;
    internal bool IsRowSet { get; private set; }
    internal int QueryIndex { get; private set; } = -1;
    internal ReadOnlyMemory<RowField> Columns { get; private set; }
    internal string? CommandTag { get; private set; }
    internal bool InitialHasRows { get; private set; }
    internal long RecordsAffected => Volatile.Read(ref _affectedRows);
    internal TransactionStatus? TransactionStatus { get; private set; }
    internal bool ProtocolCompleted { get { lock (_gate) return _completion.IsCompleted; } }
    public Task Completion { get { lock (_gate) return _completion.Task; } }
    internal Task<bool> FirstPublished { get { lock (_gate) return _firstPublished.Task; } }

    CancellationToken IQueryGroup.RequestToken => RequestToken;
    bool IQueryGroup.CancellationRequested => CancellationRequested;
    bool IQueryGroup.IsAdoSession => true;
    object IQueryGroup.DeliveryGate => _gate;
    void IQueryGroup.ThrowForSend() => ThrowForSend();
    void IQueryGroup.SyncQueued() => SyncQueued();
    void IQueryGroup.SealPublished() => SealPublished();
    void IQueryGroup.RegisterOperation(MessageOperationKind kind, MpgsqlPreparedStatement? statement) => RegisterOperation(kind, statement);
    void IQueryGroup.AddWrite(OutboundWork work) => AddWrite(work);
    void IQueryGroup.RemoveWrite(OutboundWork work) => RemoveWrite(work);

    internal void CollectAffectedRows(long[]? counts)
    {
        _collectAffectedRows = true;
        _affectedRowsByQuery = counts;
    }

    internal OutboundWork SendExecution(QueryDefinition single, QueryDefinition[]? queries) => _session.SendExecution(this, single, queries);
    internal ValueTask SendPrepareAsync(MpgsqlPreparedStatement statement) => _session.SendPrepareAsync(this, statement);
    internal ValueTask SendCloseAsync(MpgsqlPreparedStatement statement) => _session.SendCloseAsync(this, statement);
    internal ValueTask SendSyncAsync() => _session.SendSyncAsync(this);

    private void ThrowForSend()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequestToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _syncQueued) != 0)
            throw new InvalidOperationException("The query group is already closing.");
    }

    private void SyncQueued()
    {
        if (Interlocked.CompareExchange(ref _syncQueued, 1, 0) != 0)
            throw new InvalidOperationException("SendSyncAsync can be called only once per group.");
    }

    private void SealPublished()
    {
        lock (_gate)
        {
            _isSealed = true;
            _firstPublished.TrySetResult();
        }
    }

    private void AddWrite(OutboundWork work)
    {
        bool cancel;
        lock (_gate)
        {
            if (_pendingWrite is null)
                _pendingWrite = work;
            else if (!ReferenceEquals(_pendingWrite, work))
                (_pendingWrites ??= []).Add(work);
            cancel = CancellationRequested;
        }
        if (cancel)
            work.Cancel();
    }

    private void RemoveWrite(OutboundWork work)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_pendingWrite, work))
                _pendingWrite = null;
            else
                _pendingWrites?.Remove(work);
        }
    }

    private void RegisterOperation(MessageOperationKind kind, MpgsqlPreparedStatement? statement)
    {
        lock (_gate)
        {
            int? index = kind is MessageOperationKind.Query or MessageOperationKind.PreparedQuery ? _queryCount++ : null;
            if (kind == MessageOperationKind.Query && (_responses?.Count ?? 0) == 0)
            {
                if (_plainResponseCount == 0)
                    _firstResponseIndex = index!.Value;
                _plainResponseCount++;
            }
            else
                (_responses ??= new()).Enqueue(new PendingResponse(kind, statement, index));
            _firstPublished.TrySetResult(true);
            if (_phase == Phase.Idle)
                StartResponse();
        }
    }

    internal void CompleteIfUnpublished()
    {
        lock (_gate)
        {
            if (_syncQueued != 0 || _pendingWrite is not null || _pendingWrites?.Count > 0 || _queryCount != 0)
                return;
            _firstPublished.TrySetResult();
            _completion.TrySetResult();
        }
    }

    internal void BeginDiscard() => Volatile.Write(ref _discard, 1);

    internal void ProcessCancellation()
    {
        OutboundWork? pending;
        OutboundWork[]? writes;
        lock (_gate)
        {
            _cancelled = true;
            pending = _pendingWrite;
            writes = _pendingWrites?.ToArray();
        }
        pending?.Cancel();
        if (writes is not null)
            foreach (var work in writes)
                work.Cancel();
    }

    internal void AcceptError(DiagnosticMessage diagnostics, bool terminal)
    {
        lock (_gate)
        {
            if (_phase == Phase.Recovery && !terminal)
                Unexpected(BackendMessageKind.ErrorResponse);
            _error = diagnostics;
            _errorIndex = TryPeekResponse(out var response) ? response.QueryIndex : null;
            _phase = Phase.Recovery;
        }
        if (!terminal && RecoveryTimeout > TimeSpan.Zero)
            _ = BoundErrorRecoveryAsync();
    }

    private async Task BoundErrorRecoveryAsync()
    {
        try { await Completion.WaitAsync(RecoveryTimeout).ConfigureAwait(false); }
        catch (TimeoutException error) { _session.Abort(error); }
        catch { }
    }

    // Control frames are relatively infrequent and share the publication gate.
    // Complete buffered DataRow frames use ValidateRow directly, without this gate.
    internal CursorEvent Accept(BackendMessage message, bool draining)
    {
        MpgsqlPreparedStatement? prepared = null, closed = null;
        MpgsqlPreparedStatement[]? failedPreparations = null;
        Exception? completionError = null;
        var result = CursorEvent.None;
        lock (_gate)
        {
            if (message.Kind == BackendMessageKind.ReadyForQuery)
            {
                if (!_isSealed || _phase != Phase.Recovery && (_phase != Phase.Idle || HasResponses))
                    Unexpected(message.Kind);
                TransactionStatus = message.GetTransactionStatus();
                completionError = _error is { } diagnostic ? new MpgsqlServerException(diagnostic, _errorIndex, TransactionStatus.Value) : null;
                if (completionError is not null)
                    failedPreparations = CapturePreparations();
                ClearResponses();
                result = CursorEvent.ProtocolEnd;
            }
            else
            {
                if (!HasResponses || _phase == Phase.Recovery)
                    Unexpected(message.Kind);
                _ = TryPeekResponse(out var response);
                switch (_phase, message.Kind)
                {
                case (Phase.Parse, BackendMessageKind.ParseComplete):
                    if (response.Kind == MessageOperationKind.Prepare)
                    {
                        prepared = response.Statement;
                        EndResponse();
                    }
                    else
                        _phase = Phase.Bind;
                    break;
                case (Phase.Close, BackendMessageKind.CloseComplete):
                    closed = response.Statement;
                    EndResponse();
                    break;
                case (Phase.Bind, BackendMessageKind.BindComplete):
                    _phase = Phase.Describe;
                    break;
                case (Phase.Describe, BackendMessageKind.RowDescription):
                    if (draining || DiscardsRows)
                    {
                        var description = new WireReader(message.Payload);
                        _columnCount = description.Count();
                    }
                    else
                    {
                        var columns = _session.DecodeRowDescription(message);
                        foreach (ref readonly var column in columns.Span)
                            if (column.Format != FormatCode.Binary)
                                throw new InvalidDataException("A binary result has a text column.");
                        SetColumns(columns);
                        _columnCount = columns.Length;
                    }
                    _hasRows = true;
                    if (!draining && !DiscardsRows)
                    {
                        QueryIndex = response.QueryIndex!.Value;
                        IsRowSet = true;
                        CommandTag = null;
                        _resultEnd = false;
                    }
                    _phase = Phase.Rows;
                    result = CursorEvent.Description;
                    break;
                case (Phase.Describe, BackendMessageKind.NoData):
                    if (!draining && !DiscardsRows)
                        SetColumns(default);
                    _columnCount = 0;
                    _hasRows = false;
                    if (!draining && !DiscardsRows)
                    {
                        QueryIndex = response.QueryIndex!.Value;
                        IsRowSet = false;
                        CommandTag = null;
                        _resultEnd = false;
                    }
                    _phase = Phase.Rows;
                    result = CursorEvent.Description;
                    break;
                case (Phase.Rows, BackendMessageKind.CommandComplete):
                case (Phase.Rows, BackendMessageKind.EmptyQueryResponse):
                    var tag = message.Kind == BackendMessageKind.CommandComplete && (!DiscardsRows || _collectAffectedRows)
                        ? _session.DecodeCommandTag(message) : null;
                    if (_collectAffectedRows)
                    {
                        var rows = ResultConsumption.AffectedRows(tag) ?? -1;
                        if (_affectedRowsByQuery is { } counts)
                            counts[response.QueryIndex!.Value] = rows;
                        if (rows >= 0)
                            Volatile.Write(ref _affectedRows, checked(Math.Max(0, _affectedRows) + rows));
                    }
                    if (!draining && !DiscardsRows)
                    {
                        CommandTag = tag;
                        _resultEnd = true;
                    }
                    EndResponse();
                    result = CursorEvent.End;
                    break;
                default:
                    Unexpected(message.Kind);
                    break;
                }
            }
        }
        // Handle confirmations can remove a session-owned statement. Never hold
        // the cursor gate while acquiring the session/statement lifecycle gates.
        prepared?.ConfirmPrepared();
        closed?.ConfirmClosed();
        if (failedPreparations is not null)
            foreach (var statement in failedPreparations)
                statement.FailPreparation(completionError!);
        if (result == CursorEvent.ProtocolEnd)
        {
            // Publish the session's recovery boundary before observers can see
            // completion. Lifecycle callbacks and this session gate are entered
            // after releasing the cursor gate.
            _session.RecordAdoReadyForQuery(this, TransactionStatus!.Value);
            lock (_gate)
            {
                if (completionError is null)
                    _completion.TrySetResult();
                else
                    _completion.TrySetException(completionError);
            }
        }
        return result;
    }

    internal void ValidateRow(int count)
    {
        if (_phase != Phase.Rows || !_hasRows || _columnCount != count)
            throw new InvalidDataException("DataRow does not match the active portal description.");
    }

    private bool HasResponses => _plainResponseCount != 0 || (_responses?.Count ?? 0) != 0;
    private bool TryPeekResponse(out PendingResponse response)
    {
        if (_plainResponseCount != 0)
        {
            response = new PendingResponse(MessageOperationKind.Query, null, _firstResponseIndex);
            return true;
        }
        if (_responses is not null)
            return _responses.TryPeek(out response);
        response = default;
        return false;
    }

    private void StartResponse()
    {
        _phase = TryPeekResponse(out var response) ? response.Kind switch
        {
            MessageOperationKind.Query or MessageOperationKind.Prepare => Phase.Parse,
            MessageOperationKind.PreparedQuery => Phase.Bind,
            MessageOperationKind.Close => Phase.Close,
            _ => throw new InvalidDataException("Sync does not have an operation response.")
        } : Phase.Idle;
    }

    private void EndResponse()
    {
        if (_plainResponseCount != 0)
        {
            _plainResponseCount--;
            _firstResponseIndex++;
        }
        else
            _responses!.Dequeue();
        StartResponse();
    }

    private void ClearResponses()
    {
        _plainResponseCount = 0;
        _responses?.Clear();
    }

    private MpgsqlPreparedStatement[]? CapturePreparations()
    {
        if (_responses is null)
            return null;
        List<MpgsqlPreparedStatement>? statements = null;
        foreach (var response in _responses)
            if (response.Kind == MessageOperationKind.Prepare)
                (statements ??= []).Add(response.Statement!);
        return statements?.ToArray();
    }

    internal void Fail(Exception error)
    {
        OutboundWork? pending;
        OutboundWork[]? writes;
        MpgsqlPreparedStatement[]? preparations;
        lock (_gate)
        {
            if (_error is { } diagnostics && TransactionStatus is null)
            {
                var terminal = error as MpgsqlServerException;
                error = new MpgsqlServerException(terminal?.Diagnostics ?? diagnostics, _errorIndex, null,
                    terminal is null ? error : terminal.InnerException);
            }
            BeginDiscard();
            pending = _pendingWrite;
            writes = _pendingWrites?.ToArray();
            preparations = CapturePreparations();
            _firstPublished.TrySetResult();
            _completion.TrySetException(error);
        }
        // The independent writer acknowledgement still gates release of encoder
        // inputs. Its queued preparation failure may acquire the session gate.
        pending?.FailQueued(error);
        if (writes is not null)
            foreach (var work in writes)
                work.FailQueued(error);
        if (preparations is not null)
            foreach (var statement in preparations)
                statement.FailPreparation(error);
    }

    internal async ValueTask ObserveCompletionAsync()
    {
        try { await Completion.ConfigureAwait(false); }
        catch
        {
            if (Interlocked.Exchange(ref _errorObserved, 1) == 0)
                throw;
        }
    }

    internal ValueTask<bool> InitializeAsync()
    {
        if (_initialized)
            throw new InvalidOperationException("The ADO cursor can be initialized only once.");
        _initialized = true;
        return MoveAsync(Movement.FirstResult, prefetchFirstRow: true);
    }

    internal ValueTask<bool> ReadAsync()
    {
        var entered = false;
        try
        {
            Enter();
            entered = true;
            ReleaseCurrent();
            RequestToken.ThrowIfCancellationRequested();
            if (_finished || _resultEnd)
            {
                Exit();
                return new(false);
            }
            if (_session.TryReadAdoRow(this))
            {
                if (_disposed)
                {
                    _session.ReleaseAdoRow();
                    throw new ObjectDisposedException(nameof(AdoCursor));
                }
                _borrowedRow = true;
                Exit();
                return new(true);
            }
            return MoveAsync(Movement.Row, entered: true);
        }
        catch (Exception error)
        {
            if (!entered)
                return ValueTask.FromException<bool>(error);
            Exit();
            return FailMovementAsync(error);
        }
    }

    internal ValueTask<bool> NextResultAsync() => MoveAsync(Movement.NextResult);

    // A provider movement can consume several rows while holding this same claim.
    // It must release the claim before awaiting cancellation or protocol recovery.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EnterReaderMovement() => Enter();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ExitReaderMovement() => Exit();

    internal ValueTask<bool> ReadWithinMovementAsync()
    {
        ReleaseCurrent();
        RequestToken.ThrowIfCancellationRequested();
        if (_finished || _resultEnd)
            return new(false);
        if (_session.TryReadAdoRow(this))
        {
            if (_disposed)
            {
                _session.ReleaseAdoRow();
                throw new ObjectDisposedException(nameof(AdoCursor));
            }
            _borrowedRow = true;
            return new(true);
        }
        return MoveAsync(Movement.Row, entered: true, ownsMovement: false);
    }

    internal ValueTask<bool> NextResultWithinMovementAsync()
    {
        ReleaseCurrent();
        return MoveAsync(Movement.NextResult, entered: true, ownsMovement: false);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<bool> MoveAsync(Movement movement, bool entered = false, bool prefetchFirstRow = false, bool ownsMovement = true)
    {
        if (!entered)
            Enter();
        var result = false;
        Exception? error = null;
        try
        {
            if (!entered)
                ReleaseCurrent();
            RequestToken.ThrowIfCancellationRequested();
            var description = movement == Movement.FirstResult || _resultEnd;
            if (!_finished && (movement != Movement.Row || !_resultEnd))
            {
                while (true)
                {
                    RequestToken.ThrowIfCancellationRequested();
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (_session.TryReadAdoCursorEvent(this, out var value))
                    {
                        if (_disposed)
                        {
                            if (value == CursorEvent.Row)
                                _session.ReleaseAdoRow();
                            throw new ObjectDisposedException(nameof(AdoCursor));
                        }
                        if (description)
                        {
                            if (value != CursorEvent.Description)
                                throw new InvalidDataException("Expected a result description.");
                            if (prefetchFirstRow)
                            {
                                description = false;
                                movement = Movement.Row;
                                continue;
                            }
                            result = true;
                            break;
                        }
                        if (value == CursorEvent.Row)
                        {
                            if (movement == Movement.Row)
                            {
                                _borrowedRow = true;
                                result = true;
                                break;
                            }
                            _session.ReleaseAdoRow();
                            continue;
                        }
                        if (value != CursorEvent.End)
                            throw new InvalidDataException("Unexpected result description inside rows.");
                        if (movement == Movement.Row)
                            break;
                        description = true;
                        continue;
                    }
                    bool available;
                    try { available = await _session.WaitForAdoInputAsync(this).ConfigureAwait(false); }
                    catch
                    {
                        if (!ProtocolCompleted && !RequestToken.IsCancellationRequested)
                            throw;
                        RequestToken.ThrowIfCancellationRequested();
                        await ObserveCompletionAsync().ConfigureAwait(false);
                        available = false;
                    }
                    if (available)
                        continue;
                    RequestToken.ThrowIfCancellationRequested();
                    await ObserveCompletionAsync().ConfigureAwait(false);
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (!description)
                        throw new InvalidDataException("Result ended without CommandComplete.");
                    _finished = _resultEnd = true;
                    break;
                }
            }
        }
        catch (Exception failure)
        {
            _finished = _resultEnd = true;
            error = failure;
        }
        finally
        {
            if (ownsMovement)
                Exit();
        }
        if (error is not null)
        {
            if (!ownsMovement)
                ExceptionDispatchInfo.Throw(error);
            return await FailMovementAsync(error).ConfigureAwait(false);
        }
        if (prefetchFirstRow)
            InitialHasRows = result;
        if (ownsMovement && _finished && movement == Movement.NextResult)
            await _execution.EndReaderAsync(false).ConfigureAwait(false);
        return result;
    }

    private async ValueTask<bool> FailMovementAsync(Exception error)
    {
        try { await _execution.EndReaderAsync(true).ConfigureAwait(false); }
        catch { }
        ExceptionDispatchInfo.Throw(error);
        return false;
    }

    private void SetColumns(ReadOnlyMemory<RowField> columns)
    {
        Columns = columns;
        if (MemoryMarshal.TryGetArray(columns, out var storage))
        {
            _columnArray = storage.Array;
            _columnArrayOffset = storage.Offset;
        }
        else
        {
            _columnArray = null;
            _columnArrayOffset = 0;
        }
    }

    internal ReadOnlySequence<byte>? GetRawValue(int ordinal)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_borrowedRow)
            throw new InvalidOperationException("ReadAsync must position the reader on a row.");
        return _session.AdoRow.GetValue(ordinal);
    }

    internal bool IsDBNull(int ordinal) => GetRawValue(ordinal) is null;
    internal T GetFieldValue<T>(int ordinal) => GetFieldValue<T>(ordinal, null);
    internal T GetFieldValue<T>(int ordinal, MpgsqlTypeMapper? mapper)
    {
        var payload = GetRawValue(ordinal);
        var column = Columns.Span[ordinal];
        if (column.Format != FormatCode.Binary)
            throw new InvalidCastException($"Column {ordinal} cannot be read as {typeof(T)}.");
        var builtin = FieldValueDecoder<T>.Supports(column.DataTypeOid);
        Func<ReadOnlySequence<byte>, T>? custom = null;
        if (!builtin && (mapper is null || !mapper.TryGetReader(column.DataTypeOid, out custom)))
            throw new InvalidCastException($"Column {ordinal} cannot be read as {typeof(T)}.");
        if (payload is not { } bytes)
        {
            if (default(T) is null)
                return default!;
            throw new InvalidOperationException("SQL NULL requires a nullable CLR representation.");
        }
        return builtin ? FieldValueDecoder<T>.Read(column.DataTypeOid, bytes) : custom!(bytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetBorrowedBigint(int ordinal, out long? value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        value = null;
        if (!_borrowedRow || !_session.AdoRow.TryGetContiguousValue(ordinal, out var bytes, out var isNull))
            return false;
        var columns = _columnArray;
        ref readonly var column = ref (columns is not null ? ref columns[ordinal + _columnArrayOffset] : ref Columns.Span[ordinal]);
        if (column.Format != FormatCode.Binary || column.DataTypeOid != Int64Converter.TypeOid)
            return false;
        value = isNull ? null : Int64Converter.Read(bytes);
        return true;
    }

    internal ReadOnlyMemory<long>? GetInt64Array(int ordinal)
    {
        var bytes = RequireType(ordinal, Int64ArrayConverter.ArrayTypeOid);
        return bytes is { } payload ? Int64ArrayConverter.Read(payload) : null;
    }

    internal ReadOnlyMemory<long?>? GetNullableInt64Array(int ordinal)
    {
        var bytes = RequireType(ordinal, NullableInt64ArrayConverter.ArrayTypeOid);
        return bytes is { } payload ? NullableInt64ArrayConverter.Read(payload) : null;
    }

    private ReadOnlySequence<byte>? RequireType(int ordinal, uint oid)
    {
        var payload = GetRawValue(ordinal);
        var column = Columns.Span[ordinal];
        if (column.Format != FormatCode.Binary || column.DataTypeOid != oid)
            throw new InvalidCastException($"Column {ordinal} is not binary PostgreSQL type {oid}.");
        return payload;
    }

    private void Enter()
    {
        var previous = Interlocked.CompareExchange(ref _movementState, ActiveMovement, 0);
        if (previous != 0)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            throw new InvalidOperationException("Concurrent reader movement or disposal is not supported.");
        }
        if (!_disposed)
            return;
        Exit();
        throw new ObjectDisposedException(nameof(AdoCursor));
    }

    private void Exit()
    {
        if (Interlocked.CompareExchange(ref _movementState, 0, ActiveMovement) != ActiveMovement)
            FinishClosing();
    }

    private void FinishClosing()
    {
        try { ReleaseCurrent(); }
        finally
        {
            Volatile.Write(ref _movementState, Closed);
            Volatile.Read(ref _idle)?.TrySetResult();
        }
    }

    internal Task InvalidateFromOwner()
    {
        _disposed = true;
        var state = Volatile.Read(ref _movementState);
        while (true)
        {
            if (state == Closed)
                return Task.CompletedTask;
            if (state == (Closed | ActiveMovement))
                return WaitForClosing();
            var previous = Interlocked.CompareExchange(ref _movementState, Closed | ActiveMovement, state);
            if (previous != state)
            {
                state = previous;
                continue;
            }
            if (state == ActiveMovement)
                return WaitForClosing();
            FinishClosing();
            return Task.CompletedTask;
        }
    }

    private Task WaitForClosing()
    {
        var idle = Volatile.Read(ref _idle);
        if (idle is null)
        {
            var created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            idle = Interlocked.CompareExchange(ref _idle, created, null) ?? created;
        }
        if (Volatile.Read(ref _movementState) == Closed)
            idle.TrySetResult();
        return idle.Task;
    }

    internal void ReleaseCurrent()
    {
        if (_borrowedRow)
        {
            _borrowedRow = false;
            _session.ReleaseAdoRow();
        }
    }

    public async ValueTask DisposeAsync()
    {
        BeginDiscard();
        await InvalidateFromOwner().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _syncQueued) != 0 && !ProtocolCompleted)
                await _session.DrainAdoAsync(this).ConfigureAwait(false);
            if (Volatile.Read(ref _syncQueued) != 0 && !RequestToken.IsCancellationRequested)
                await ObserveCompletionAsync().ConfigureAwait(false);
        }
        finally { _session.ReleaseAdoCursor(this); }
    }

    private void Unexpected(BackendMessageKind kind) => throw new InvalidDataException($"Unexpected {kind} in phase {_phase}.");
    private enum Phase { Idle, Parse, Bind, Describe, Rows, Close, Recovery }
    private enum Movement { FirstResult, Row, NextResult }
}

internal enum CursorEvent { None, Description, Row, End, ProtocolEnd }
