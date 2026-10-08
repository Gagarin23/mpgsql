using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Mpgsql.Internal;

namespace Mpgsql.Multiplexing.Internal;

// Single-use owner of producer, consumer, recovery, and the scheduling/connection lease.
internal sealed class QueryExecution : IResultExecutionOwner
{
    private readonly MpgsqlQueryBatch _batch;
    private readonly Lock _gate = new Lock();

    private readonly PooledSession _pooled;

    // Also the borrowed-input barrier. A single query hands Sync delivery to recovery.
    private readonly Task _producer;
    private readonly CancellationToken _request;
    private readonly MpgsqlMultiplexingDataSource _source;
    private bool _discard;
    private Task? _finish;
    private int _finishErrorObserved;
    private TaskCompletionSource? _finishSignal;
    private bool _finishingStarted;
    private MpgsqlResultReader? _reader;
    private Task _readerIdle = Task.CompletedTask;
    private OutboundWork? _send;
    private SharedSyncGroup? _syncGroup;

    internal QueryExecution(MpgsqlMultiplexingDataSource source, PooledSession pooled,
        QueryDefinition query, CancellationToken request)
        : this(source, pooled, query, null, request) { }

    internal QueryExecution(MpgsqlMultiplexingDataSource source, PooledSession pooled,
        QueryDefinition query, QueryDefinition[]? queries,
        CancellationToken request)
    {
        _source = source;
        _pooled = pooled;
        _request = request;
        _batch = pooled.Session.CreateBatch(request);
        _producer = ProduceAsync(query, queries);
    }
    internal QueryExecution(MpgsqlMultiplexingDataSource source, PooledSession pooled,
        QueryDefinition[] queries, CancellationToken request)
        : this(source, pooled, default, queries, request) { }

    private Task SyncDelivery => _syncGroup?.Delivery ?? _send?.Delivery ?? Task.CompletedTask;

    public ValueTask EndReaderAsync(bool discard)
    {
        return _request.IsCancellationRequested ? EndCancelledReaderAsync() : FinishAsync(discard);
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
            if (_pooled.SyncScheduler is { } scheduler)
            {
                (work, _syncGroup) = scheduler.Enqueue(_batch, single);
            }
            else
            {
                work = _batch.SendExecution(single, queries);
            }
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
                if (_discard)
                {
                    throw new ObjectDisposedException("Command or connection");
                }
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
            try { await EndReaderAsync(true).ConfigureAwait(false); }
            catch
            {
                /* retain the original read/admission failure */
            }
            throw;
        }
    }

    private async ValueTask EndCancelledReaderAsync()
    {
        // Wait only for input release; blocked Sync delivery and ReadyForQuery retain the slot
        // in the background. Never cancel the shared transport's FlushAsync for this request.
        _ = ObserveBackgroundFinishAsync();
        try { await _producer.ConfigureAwait(false); }
        catch { }
    }

    private async Task ObserveBackgroundFinishAsync()
    {
        try { await FinishAsync(true).ConfigureAwait(false); }
        catch { }
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
                if (_finish?.IsCompletedSuccessfully != true)
                {
                    _batch.BeginDiscard();
                }
                _readerIdle = _reader?.InvalidateFromOwner() ?? Task.CompletedTask;
            }
            finishing = _finish;
            start = !_finishingStarted;
            if (start)
            {
                _finishingStarted = true;
            }
            else if (finishing is null)
            {
                finishing = (_finishSignal ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }
        }
        if (start)
        {
            // Lease release can take the source gate; start outside the execution gate.
            finishing = FinishCoreAsync();
            TaskCompletionSource? signal;
            lock (_gate)
            {
                _finish = finishing;
                signal = _finishSignal;
                _finishSignal = null;
            }
            // Only a concurrent observer in the short startup gap needs a forwarding promise.
            if (signal is not null)
            {
                _ = CompleteFinishAsync(signal, finishing);
            }
        }
        return finishing!.IsCompletedSuccessfully ? ValueTask.CompletedTask : new ValueTask(ObserveFinishAsync(finishing));
    }

    private async Task ObserveFinishAsync(Task finishing)
    {
        try { await finishing.ConfigureAwait(false); }
        catch
        {
            if (Interlocked.Exchange(ref _finishErrorObserved, 1) == 0)
            {
                throw;
            }
        }
    }

    private static async Task CompleteFinishAsync(TaskCompletionSource signal, Task finishing)
    {
        try
        {
            await finishing.ConfigureAwait(false);
            signal.TrySetResult();
        }
        catch (Exception error) { signal.TrySetException(error); }
    }

    private async Task FinishProtocolAsync()
    {
        Exception? error = null;
        try { await _producer.ConfigureAwait(false); }
        catch (Exception ex) { error = ex; }
        try { await SyncDelivery.ConfigureAwait(false); }
        catch (Exception ex) { error ??= ex; }
        try { await _batch.ObserveCompletionAsync().ConfigureAwait(false); }
        catch (Exception ex) { error ??= ex; }
        if (error is not null && !_request.IsCancellationRequested)
        {
            ExceptionDispatchInfo.Throw(error);
        }
    }

    private async Task FinishCoreAsync()
    {
        Exception? error = null;
        try
        {
            var finishing = FinishProtocolAsync();
            await finishing.ConfigureAwait(false);
        }
        catch (Exception ex) { error = ex; }
        finally
        {
            Task idle;
            lock (_gate)
            {
                idle = _readerIdle;
            }
            await idle.ConfigureAwait(false);
            _reader?.ReleaseCurrent();
            try { await _batch.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { error ??= ex; }
            await _source.ReleaseRequestAsync(_pooled).ConfigureAwait(false);
        }
        if (error is not null && !_request.IsCancellationRequested)
        {
            ExceptionDispatchInfo.Throw(error);
        }
    }
}