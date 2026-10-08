using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Mpgsql.Internal;

// Single-use owner of producer, consumer, recovery, and the scheduling/connection lease.
internal sealed class QueryExecution
{
    private readonly MpgsqlDataSource _source;
    private readonly PooledSession _pooled;
    private readonly MpgsqlConnection? _connection;
    private readonly MpgsqlQueryBatch _batch;
    private readonly CancellationToken _request;
    private readonly CancellationTokenRegistration _registration;
    private readonly TaskCompletionSource? _cancel;
    // Also the borrowed-input barrier. A single query hands Sync delivery to recovery.
    private readonly Task _producer;
    private readonly Task _control;
    private OutboundWork? _send;
    private SharedSyncGroup? _syncGroup;
    private readonly Lock _gate = new();
    private Task? _finish;
    private bool _finishingStarted;
    private TaskCompletionSource? _finishSignal;
    private MpgsqlResultReader? _reader;
    private Task _readerIdle = Task.CompletedTask;
    private bool _discard;
    private int _finishErrorObserved;

    internal QueryExecution(MpgsqlDataSource source, PooledSession pooled, MpgsqlConnection? connection,
        QueryDefinition[] queries, CancellationToken request)
        : this(source, pooled, connection, queries.Length == 1 ? queries[0] : default,
            queries.Length == 1 ? null : queries, request) { }

    internal QueryExecution(MpgsqlDataSource source, PooledSession pooled,
        QueryDefinition query, CancellationToken request)
        : this(source, pooled, null, query, null, request) { }

    private QueryExecution(MpgsqlDataSource source, PooledSession pooled, MpgsqlConnection? connection,
        QueryDefinition query, QueryDefinition[]? queries, CancellationToken request)
    {
        _source = source;
        _pooled = pooled;
        _connection = connection;
        _request = request;
        _batch = pooled.Session.CreateBatch(request);
        bool serverCancellation = connection is not null && request.CanBeCanceled;
        if (serverCancellation)
        {
            _cancel = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _registration = request.UnsafeRegister(static state => ((TaskCompletionSource)state!).TrySetResult(), _cancel);
        }
        _producer = ProduceAsync(query, queries);
        // User-supplied cancel transport code must never run on the network reader or token callback.
        _control = serverCancellation ? Task.Run(ControlCancellationAsync) : Task.CompletedTask;
    }

    private Task ProduceAsync(QueryDefinition single, QueryDefinition[]? queries)
    {
        try
        {
            _request.ThrowIfCancellationRequested();
            // Independent upper executions enqueue Query + Sync atomically in the session.
            // Shared boundaries have their own scheduler gate. Neither needs another
            // per-transport semaphore before admission.
            OutboundWork work;
            if (_connection is null && _pooled.SyncScheduler is { } scheduler)
            {
                (work, _syncGroup) = scheduler.Enqueue(_batch, single);
            }
            else work = _batch.SendExecution(single, queries);
            _send = work;
            // The complete boundary is queued atomically. On logical cancellation,
            // Completion releases borrowed inputs promptly; Delivery still protects Sync recovery.
            // Both admission paths mark Sync queued before returning. CompleteIfUnpublished
            // would therefore be a no-op after this task, even on failure. Share the existing
            // acknowledgement rather than allocate a forwarding task and continuation.
            return work.Completion;
        }
        catch (Exception error)
        {
            return FailProductionAsync(error);
        }
    }

    private async Task FailProductionAsync(Exception error)
    {
        // Retain asynchronous exception/cancellation capture if admission never succeeded.
        // In particular, constructor failure must not strand an unpublished batch or lease.
        await Task.CompletedTask.ConfigureAwait(false);
        _batch.CompleteIfUnpublished();
        ExceptionDispatchInfo.Throw(error);
    }

    // Entry points forward successful operations, but keep asynchronous exception/cancellation
    // capture on failure. FromException would turn an OperationCanceledException into a fault.
    internal static async ValueTask<MpgsqlResultReader> ReaderFailureAsync(Exception error)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        ExceptionDispatchInfo.Throw(error);
        return null!;
    }

    private async Task ControlCancellationAsync()
    {
        Task cancelled = _cancel!.Task;
        await Task.WhenAny(cancelled, _batch.Completion).ConfigureAwait(false);
        if (!cancelled.IsCompleted || _batch.Completion.IsCompleted) return;
        using var recovery = CancellationTokenSource.CreateLinkedTokenSource(_source.LifetimeToken);
        recovery.CancelAfter(_source.Options.RecoveryTimeout);
        try
        {
            bool published = await _batch.FirstPublished.WaitAsync(recovery.Token).ConfigureAwait(false);
            if (published && !_batch.Completion.IsCompleted)
                await _source.SendCancelRequest(_pooled.Session, recovery.Token).AsTask().WaitAsync(recovery.Token).ConfigureAwait(false);
            await _producer.WaitAsync(recovery.Token).ConfigureAwait(false);
            await SyncDelivery.WaitAsync(recovery.Token).ConfigureAwait(false);
            try { await _batch.Completion.WaitAsync(recovery.Token).ConfigureAwait(false); }
            catch (MpgsqlServerException error) when (error.TransactionStatus is not null)
            { /* expected server cancellation/error at a confirmed ReadyForQuery boundary */ }
        }
        catch { await _source.RetireAsync(_pooled).ConfigureAwait(false); }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    internal async ValueTask<MpgsqlResultReader> OpenReaderAsync(bool waitForInputRelease = false)
    {
        try
        {
            var reader = await _batch.ReadResultsAsync().ConfigureAwait(false);
            lock (_gate)
            {
                _reader = reader;
                reader.Execution = this;
                if (_discard) throw new ObjectDisposedException("Command or connection");
            }
            if (waitForInputRelease)
            {
                await _producer.ConfigureAwait(false);
                _request.ThrowIfCancellationRequested();
            }
            return reader;
        }
        catch
        {
            try { await EndReaderAsync(discard: true).ConfigureAwait(false); }
            catch { /* retain the original read/admission failure */ }
            throw;
        }
    }

    internal async ValueTask EndReaderAsync(bool discard)
    {
        if (_connection is null && _request.IsCancellationRequested)
        {
            // Wait only for input release; blocked Sync delivery and ReadyForQuery retain the slot
            // in the background. Never cancel the shared transport's FlushAsync for this request.
            _ = ObserveBackgroundFinishAsync();
            try { await _producer.ConfigureAwait(false); } catch { }
            return;
        }
        await FinishAsync(discard).ConfigureAwait(false);
    }

    private async Task ObserveBackgroundFinishAsync()
    {
        try { await FinishAsync(discard: true).ConfigureAwait(false); } catch { }
    }

    internal ValueTask FinishAsync(bool discard)
    {
        Task? finishing;
        bool start;
        lock (_gate)
        {
            if (discard && !_discard)
            {
                _discard = true;
                if (_finish?.IsCompletedSuccessfully != true) _batch.BeginDiscard();
                _readerIdle = _reader?.InvalidateFromOwner() ?? Task.CompletedTask;
            }
            finishing = _finish;
            start = !_finishingStarted;
            if (start) _finishingStarted = true;
            else if (finishing is null)
                finishing = (_finishSignal ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
        if (start)
        {
            // Cleanup may acquire Connection.Gate. Start outside this gate to avoid lock inversion
            // with owner disposal. Most finishes need no separate promise or forwarding task.
            finishing = FinishCoreAsync(discard);
            TaskCompletionSource? signal;
            lock (_gate) { _finish = finishing; signal = _finishSignal; _finishSignal = null; }
            // Only a concurrent observer in the short startup gap needs a forwarding promise.
            if (signal is not null) _ = CompleteFinishAsync(signal, finishing);
        }
        return finishing!.IsCompletedSuccessfully ? ValueTask.CompletedTask : new(ObserveFinishAsync(finishing));
    }

    private async Task ObserveFinishAsync(Task finishing)
    {
        try { await finishing.ConfigureAwait(false); }
        catch { if (Interlocked.Exchange(ref _finishErrorObserved, 1) == 0) throw; }
    }

    private static async Task CompleteFinishAsync(TaskCompletionSource signal, Task finishing)
    {
        try { await finishing.ConfigureAwait(false); signal.TrySetResult(); }
        catch (Exception error) { signal.TrySetException(error); }
    }

    private async Task FinishProtocolAsync()
    {
        Exception? error = null;
        try { await _producer.ConfigureAwait(false); } catch (Exception ex) { error = ex; }
        try { await SyncDelivery.ConfigureAwait(false); } catch (Exception ex) { error ??= ex; }
        try { await _batch.ObserveCompletionAsync().ConfigureAwait(false); } catch (Exception ex) { error ??= ex; }
        await _control.ConfigureAwait(false);
        if (error is not null && !_request.IsCancellationRequested) ExceptionDispatchInfo.Throw(error);
    }

    private Task SyncDelivery => _syncGroup?.Delivery ?? _send?.Delivery ?? Task.CompletedTask;

    private async Task FinishCoreAsync(bool discard)
    {
        Exception? error = null;
        try
        {
            Task finishing = FinishProtocolAsync();
            if (discard && _connection is not null)
            {
                try { await finishing.WaitAsync(_source.Options.RecoveryTimeout).ConfigureAwait(false); }
                catch (TimeoutException ex)
                {
                    await _source.RetireAsync(_pooled).ConfigureAwait(false);
                    try { await finishing.ConfigureAwait(false); } catch { }
                    throw new TimeoutException("Connection recovery timed out; its transport was closed.", ex);
                }
            }
            else await finishing.ConfigureAwait(false);
        }
        catch (Exception ex) { error = ex; }
        finally
        {
            _registration.Dispose();
            Task idle;
            lock (_gate) idle = _readerIdle;
            await idle.ConfigureAwait(false);
            _reader?.ReleaseCurrent();
            try { await _batch.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { error ??= ex; }
            if (_connection is null) await _source.ReleaseRequestAsync(_pooled).ConfigureAwait(false);
            else _connection.ExecutionCompleted(this);
        }
        if (error is not null && !_request.IsCancellationRequested) ExceptionDispatchInfo.Throw(error);
    }
}
