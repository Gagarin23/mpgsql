using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Mpgsql.Internal;

// One exclusive lease, including the CancelRequest channel and the final ReadyForQuery.
internal sealed class QueryExecution : IResultExecutionOwner
{
    private readonly AdoCursor _cursor;
    private readonly Action _completed;
    private readonly MpgsqlConnection _connection;
    private readonly Lock _gate = new Lock();
    private readonly bool _ownsConnection;
    private readonly CancellationTokenRegistration _registration;
    private readonly CancellationToken _request;
    private readonly MpgsqlMessageSession _session;
    private readonly Timer? _timer;
    private CancellationToken _cancellationToken;

    private volatile bool _cancelled,
        _timedOut;

    private bool _ended,
        _discard;

    private Task? _finish;
    private int _finishErrorObserved;
    private TaskCompletionSource? _finishSignal;
    private bool _finishing;

    private Task _producer = Task.CompletedTask,
        _control = Task.CompletedTask;

    private Task _readerIdle = Task.CompletedTask;
    private OutboundWork? _send;

    internal QueryExecution(
        MpgsqlConnection connection, CancellationToken request,
        int timeout, Action completed,
        bool ownsConnection, long[]? affectedRows = null
    )
    {
        if ((uint)timeout > (uint.MaxValue - 1) / 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
        _connection = connection;
        _session = connection.Session;
        _request = _cancellationToken = request;
        _completed = completed;
        _ownsConnection = ownsConnection;
        _cursor = _session.CreateAdoCursor(request, this);
        _cursor.CollectAffectedRows(affectedRows);
        _cursor.RecoveryTimeout = connection.RecoveryTimeout;
        if (request.CanBeCanceled)
        {
            _registration = request.UnsafeRegister(static state => ((QueryExecution)state!).Cancel(), this);
        }
        if (timeout != 0)
        {
            _timer = new Timer(static state => ((QueryExecution)state!).CancelCoreReason(true, default), this, TimeSpan.FromSeconds(timeout), Timeout.InfiniteTimeSpan);
        }
    }
    internal bool IsCancellationRequested => _cancelled;

    internal bool IsEnded
    {
        get
        {
            lock (_gate)
            {
                return _ended;
            }
        }
    }

    internal long RecordsAffected => _cursor.RecordsAffected;
    internal AdoCursor Cursor => _cursor;
    internal MpgsqlConnection Connection => _connection;

    public ValueTask EndReaderAsync(bool discard)
    {
        return FinishAsync(discard);
    }

    internal void Publish(QueryDefinition single, QueryDefinition[]? queries)
    {
        try
        {
            _request.ThrowIfCancellationRequested();
            _send = _cursor.SendExecution(single, queries);
            _producer = _send.Completion;
        }
        catch (Exception error)
        {
            _cursor.CompleteIfUnpublished();
            _producer = Task.FromException(error);
        }
    }

    internal void PublishAdministration(MpgsqlPreparedStatement[] statements, bool close)
    {
        _producer = ProduceAdministrationAsync(statements, close);
    }

    private async Task ProduceAdministrationAsync(MpgsqlPreparedStatement[] statements, bool close)
    {
        try
        {
            foreach (var statement in statements)
            {
                if (close)
                {
                    await _cursor
                        .SendCloseAsync(statement)
                        .ConfigureAwait(false);
                }
                else
                {
                    await _cursor
                        .SendPrepareAsync(statement)
                        .ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await _cursor
                .SendSyncAsync()
                .ConfigureAwait(false);
        }
    }

    internal void Cancel()
    {
        CancelCoreReason(false, _request);
    }
    internal void Cancel(CancellationToken token)
    {
        CancelCoreReason(false, token);
    }
    private void CancelCoreReason(bool timedOut, CancellationToken token)
    {
        lock (_gate)
        {
            if (_ended || _cancelled)
            {
                return;
            }
            _timedOut = timedOut;
            _cancellationToken = token;
            _cancelled = true;
            _cursor.ProcessCancellation();
            if (_finishing)
            {
                _ = EnforceRecoveryAsync(_finish ?? (_finishSignal ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task);
            }
            // External transport delegates and TLS must run outside the token and reader callbacks.
            _control = Task.Run(CancelCoreAsync);
            // Serialize the wake with _ended before a recovered lease can be reused.
            if (_session.IsAdoSession)
            {
                _session.WakeAdoReader();
            }
        }
    }

    private async Task CancelCoreAsync()
    {
        using var deadline = new CancellationTokenSource(_connection.RecoveryTimeout);
        try
        {
            if (!await _cursor
                    .FirstPublished.WaitAsync(deadline.Token)
                    .ConfigureAwait(false))
            {
                return;
            }
            if (!_cursor.Completion.IsCompleted)
            {
                await _connection
                    .SendCancelAsync(_session, deadline.Token)
                    .AsTask()
                    .WaitAsync(deadline.Token)
                    .ConfigureAwait(false);
            }
            if (_session.IsAdoSession)
            {
                // Read while delivery can still be pending: cancellation does not remove
                // duplex backpressure. FinishCore awaits _control only after protocol drain.
                _ = FinishAdoCancellationAsync();
            }
            Task producer;
            OutboundWork? send;
            lock (_connection.Gate)
            {
                producer = _producer;
                send = _send;
            }
            try
            {
                await producer
                    .WaitAsync(deadline.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cancelled && !deadline.IsCancellationRequested) { }
            if (send is not null)
            {
                await send
                    .Delivery.WaitAsync(deadline.Token)
                    .ConfigureAwait(false);
            }
            try
            {
                await _cursor
                    .Completion.WaitAsync(deadline.Token)
                    .ConfigureAwait(false);
            }
            catch (MpgsqlServerException error) when (error.TransactionStatus is not null) { }
        }
        catch (Exception error)
        {
            _session.Abort(error);
            throw;
        }
    }

    private async Task FinishAdoCancellationAsync()
    {
        try
        {
            await FinishAsync(true)
                .ConfigureAwait(false);
        }
        catch { } // The public execution observes the mapped cancellation/failure.
    }

    internal Exception Map(Exception error)
    {
        // An idle terminal fault can finish the execution before cursor admission.
        // Preserve this cursor's original fault instead of its subsequent disposal.
        if (error is ObjectDisposedException && _cursor.Completion.IsFaulted)
            error = _cursor.Completion.Exception!.InnerException!;
        return _cancelled
            ? _timedOut ? new MpgsqlException("The command timed out.", new TimeoutException()) : new OperationCanceledException("The execution was canceled.", error, _cancellationToken)
            : MpgsqlException.Map(error);
    }

    internal void ThrowIfCancelled()
    {
        if (_cancelled)
        {
            ExceptionDispatchInfo.Throw(Map(new OperationCanceledException()));
        }
    }
    internal void Abort()
    {
        _session.Abort(new IOException("An active execution was disposed synchronously."));
    }

    internal void CompleteReaderInitialization()
    {
        lock (_gate)
        {
            if (_discard || _cursor.IsDisposed)
                throw new ObjectDisposedException("Execution");
        }
        ThrowIfCancelled();
    }

    internal async ValueTask<long> ExecuteNonQueryAsync()
    {
        try
        {
            await FinishAsync(false, true).ConfigureAwait(false);
            return RecordsAffected;
        }
        catch (Exception error)
        {
            await RecoverExecutionAsync().ConfigureAwait(false);
            ExceptionDispatchInfo.Throw(Map(error));
            return 0;
        }
    }

    internal async ValueTask<object?> ExecuteScalarAsync()
    {
        try
        {
            var hasRow = await ReadScalarRowAsync().ConfigureAwait(false);
            object? result = hasRow ? ReadObject(_cursor, 0) : null;
            await FinishAsync(false, true).ConfigureAwait(false);
            return result;
        }
        catch (Exception error)
        {
            await RecoverExecutionAsync().ConfigureAwait(false);
            ExceptionDispatchInfo.Throw(Map(error));
            return null;
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    internal async ValueTask<MpgsqlScalarResult<T>> ExecuteScalarAsync<T>()
    {
        try
        {
            MpgsqlScalarResult<T> result = default;
            if (await ReadScalarRowAsync().ConfigureAwait(false))
            {
                result = _cursor.IsDBNull(0)
                    ? new MpgsqlScalarResult<T>(true, default)
                    : new MpgsqlScalarResult<T>(false, ReadField<T>(_cursor, 0, _connection.TypeMapper));
            }
            await FinishAsync(false, true).ConfigureAwait(false);
            return result;
        }
        catch (Exception error)
        {
            await RecoverExecutionAsync().ConfigureAwait(false);
            ExceptionDispatchInfo.Throw(Map(error));
            return default;
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<bool> ReadScalarRowAsync()
    {
        var hasRow = await _cursor.InitializeAsync().ConfigureAwait(false);
        CompleteReaderInitialization();
        // An empty first row set still selects the scalar result. Later row sets
        // cannot supply a value for it, although FinishAsync drains them all.
        if (_cursor.IsRowSet)
            return hasRow && _cursor.Columns.Length != 0;
        while (await _cursor.NextResultAsync().ConfigureAwait(false))
        {
            ThrowIfCancelled();
            if (_cursor.IsRowSet)
            {
                hasRow = await _cursor.ReadAsync().ConfigureAwait(false);
                ThrowIfCancelled();
                return hasRow && _cursor.Columns.Length != 0;
            }
        }
        return false;
    }

    private async ValueTask RecoverExecutionAsync()
    {
        try { await FinishAsync(true).ConfigureAwait(false); }
        catch { } // Preserve the error that triggered recovery.
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static object ReadObject(AdoCursor cursor, int ordinal)
    {
        if (cursor.TryGetBorrowedBigint(ordinal, out var bigint))
            return bigint.HasValue ? (object)bigint.GetValueOrDefault() : DBNull.Value;
        var payload = cursor.GetRawValue(ordinal);
        return payload is { } bytes
            ? ResultValue.Read(cursor.Columns.Span[ordinal].DataTypeOid, bytes)
            : DBNull.Value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static T ReadField<T>(AdoCursor cursor, int ordinal, MpgsqlTypeMapper? mapper)
    {
        if (typeof(T) == typeof(long) && cursor.TryGetBorrowedBigint(ordinal, out var bigint))
            return (T)(object)(bigint ?? throw new InvalidCastException("The value is SQL NULL."));
        if (typeof(T) == typeof(object))
            return (T)ReadObject(cursor, ordinal);
        try
        {
            return mapper is null ? cursor.GetFieldValue<T>(ordinal) : cursor.GetFieldValue<T>(ordinal, mapper);
        }
        catch (InvalidOperationException) when (cursor.IsDBNull(ordinal))
        {
            throw new InvalidCastException("The value is SQL NULL.");
        }
    }
    internal ValueTask FinishAsync(bool discard, bool primaryObserver = false)
    {
        Task? finish;
        bool start,
            enforceRecovery = false;
        lock (_gate)
        {
            // _ended follows protocol completion (including its recovery attempt),
            // while the shared finish task still owns cancellation and lease cleanup.
            // A late old-reader disposal must not wake the next connection owner.
            if (discard && !_discard && !_ended)
            {
                _discard = true;
                _cursor.BeginDiscard();
                _readerIdle = _cursor.InvalidateFromOwner();
                enforceRecovery = _finishing;
            }
            finish = _finish;
            start = !_finishing;
            if (start)
            {
                _finishing = true;
            }
            else if (finish is null)
            {
                finish = (_finishSignal ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }
            // Serialize the wake itself with FinishCore's _ended publication,
            // rather than letting a captured decision outlive connection ownership.
            if (discard && !_ended && _session.IsAdoSession)
                _session.WakeAdoReader();
        }
        if (start)
        {
            finish = FinishCoreAsync();
            TaskCompletionSource? signal;
            lock (_gate)
            {
                _finish = finish;
                signal = _finishSignal;
                _finishSignal = null;
            }
            if (signal is not null)
            {
                _ = CompleteFinishAsync(signal, finish);
            }
        }
        if (enforceRecovery)
        {
            _ = EnforceRecoveryAsync(finish!);
        }
        return finish!.IsCompletedSuccessfully ? ValueTask.CompletedTask : new ValueTask(ObserveFinishAsync(finish, primaryObserver));
    }
    private async Task EnforceRecoveryAsync(Task finish)
    {
        try
        {
            await finish
                .WaitAsync(_connection.RecoveryTimeout)
                .ConfigureAwait(false);
        }
        catch (TimeoutException error) { _session.Abort(error); }
        catch { }
    }
    private async Task ObserveFinishAsync(Task finish, bool primaryObserver)
    {
        try { await finish.ConfigureAwait(false); }
        catch
        {
            if (Interlocked.Exchange(ref _finishErrorObserved, 1) == 0 || primaryObserver)
            {
                throw;
            }
        }
    }
    private static async Task CompleteFinishAsync(TaskCompletionSource signal, Task finish)
    {
        try
        {
            await finish.ConfigureAwait(false);
            signal.TrySetResult();
        }
        catch (Exception error) { signal.TrySetException(error); }
    }

    private async Task FinishProtocolAsync()
    {
        Exception? error = null;
        // Start input consumption before waiting for a pending output flush. A large
        // batch can otherwise deadlock when both TCP directions encounter backpressure.
        var draining = _session.IsAdoSession ? DrainAdoProtocolAsync() : null;
        try
        {
            await _producer.ConfigureAwait(false);
            if (_send is not null)
            {
                await _send.Delivery.ConfigureAwait(false);
            }
        }
        catch (Exception failure) { error = failure; }
        if (draining is not null)
        {
            try { await draining.ConfigureAwait(false); }
            catch (Exception failure) { error ??= failure; }
        }
        try
        {
            await _cursor
                .ObserveCompletionAsync()
                .ConfigureAwait(false);
        }
        catch (Exception failure) { error ??= failure; }
        if (error is not null)
        {
            ExceptionDispatchInfo.Throw(error);
        }
    }

    private async Task DrainAdoProtocolAsync()
    {
        await _readerIdle.ConfigureAwait(false);
        await _session
            .DrainAdoAsync(_cursor)
            .ConfigureAwait(false);
    }

    private async Task FinishCoreAsync()
    {
        Exception? error = null;
        var protocol = FinishProtocolAsync();
        try
        {
            bool recovering;
            lock (_gate)
            {
                recovering = _discard || _cancelled;
            }
            if (recovering)
            {
                await protocol
                    .WaitAsync(_connection.RecoveryTimeout)
                    .ConfigureAwait(false);
            }
            else
            {
                await protocol.ConfigureAwait(false);
            }
        }
        catch (Exception failure)
        {
            error = failure;
            if (failure is TimeoutException)
            {
                _session.Abort(failure);
                try { await protocol.ConfigureAwait(false); }
                catch { }
            }
        }
        Task control,
            idle;
        lock (_gate)
        {
            _ended = true;
            control = _control;
            idle = _readerIdle;
        }
        await _registration
            .DisposeAsync()
            .ConfigureAwait(false);
        _timer?.Dispose();
        try { await control.ConfigureAwait(false); }
        catch (Exception failure) { error ??= failure; }
        await idle.ConfigureAwait(false);
        _cursor.ReleaseCurrent();
        try
        {
            await _cursor
                .DisposeAsync()
                .ConfigureAwait(false);
        }
        catch (Exception failure) { error ??= failure; }
        _connection.ExecutionCompleted(this);
        try
        {
            if (_ownsConnection)
            {
                await _connection
                    .CloseOwnedLeaseAsync()
                    .ConfigureAwait(false);
            }
        }
        finally { _completed(); }
        if (error is not null)
        {
            ExceptionDispatchInfo.Throw(Map(error));
        }
        ThrowIfCancelled();
    }
}
