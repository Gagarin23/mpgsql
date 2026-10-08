using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Mpgsql.Internal;

// One exclusive lease, including the CancelRequest channel and the final ReadyForQuery.
internal sealed class QueryExecution : IResultExecutionOwner
{
    private readonly MpgsqlQueryBatch _batch;
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

    private MpgsqlResultReader? _reader;
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
        _batch = _session.CreateBatch(request);
        _batch.CollectAffectedRows(affectedRows);
        _batch.RecoveryTimeout = connection.RecoveryTimeout;
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

    internal long RecordsAffected => _batch.RecordsAffected;

    public ValueTask EndReaderAsync(bool discard)
    {
        return FinishAsync(discard);
    }

    internal void Publish(QueryDefinition single, QueryDefinition[]? queries)
    {
        try
        {
            _request.ThrowIfCancellationRequested();
            _send = _batch.SendExecution(single, queries);
            _producer = _send.Completion;
        }
        catch (Exception error)
        {
            _batch.CompleteIfUnpublished();
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
                    await _batch
                        .SendCloseAsync(statement)
                        .ConfigureAwait(false);
                }
                else
                {
                    await _batch
                        .SendPrepareAsync(statement)
                        .ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await _batch
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
            _batch.ProcessCancellation();
            if (_finishing)
            {
                _ = EnforceRecoveryAsync(_finish ?? (_finishSignal ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task);
            }
            // External transport delegates and TLS must run outside the token and reader callbacks.
            _control = Task.Run(CancelCoreAsync);
        }
    }

    private async Task CancelCoreAsync()
    {
        using var deadline = new CancellationTokenSource(_connection.RecoveryTimeout);
        try
        {
            if (!await _batch
                    .FirstPublished.WaitAsync(deadline.Token)
                    .ConfigureAwait(false))
            {
                return;
            }
            if (!_batch.Completion.IsCompleted)
            {
                await _connection
                    .SendCancelAsync(_session, deadline.Token)
                    .AsTask()
                    .WaitAsync(deadline.Token)
                    .ConfigureAwait(false);
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
                await _batch
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

    internal Exception Map(Exception error)
    {
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

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    internal async ValueTask<MpgsqlResultReader> OpenReaderAsync()
    {
        try
        {
            var reader = await _batch
                .ReadResultsAsync()
                .ConfigureAwait(false);
            lock (_gate)
            {
                _reader = reader;
                reader.Execution = this;
                if (_discard)
                {
                    throw new ObjectDisposedException("Execution");
                }
            }
            ThrowIfCancelled();
            return reader;
        }
        catch (Exception error)
        {
            try
            {
                await FinishAsync(true)
                    .ConfigureAwait(false);
            }
            catch { }
            ExceptionDispatchInfo.Throw(Map(error));
            return null!;
        }
    }

    internal ValueTask FinishAsync(bool discard, bool primaryObserver = false)
    {
        Task? finish;
        bool start,
            enforceRecovery = false;
        lock (_gate)
        {
            if (discard && !_discard)
            {
                _discard = true;
                _batch.BeginDiscard();
                _readerIdle = _reader?.InvalidateFromOwner() ?? Task.CompletedTask;
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
        try
        {
            await _producer.ConfigureAwait(false);
            if (_send is not null)
            {
                await _send.Delivery.ConfigureAwait(false);
            }
        }
        catch (Exception failure) { error = failure; }
        try
        {
            await _batch
                .ObserveCompletionAsync()
                .ConfigureAwait(false);
        }
        catch (Exception failure) { error ??= failure; }
        if (error is not null)
        {
            ExceptionDispatchInfo.Throw(error);
        }
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
        _reader?.ReleaseCurrent();
        try
        {
            await _batch
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