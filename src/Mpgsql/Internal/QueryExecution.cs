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
    private readonly Task _producer;
    private readonly Task _control;
    private readonly Lock _gate = new();
    private Task? _finish;
    private MpgsqlResultReader? _reader;
    private Task _readerIdle = Task.CompletedTask;
    private bool _discard;
    private int _finishErrorObserved;

    internal QueryExecution(MpgsqlDataSource source, PooledSession pooled, MpgsqlConnection? connection,
        QueryDefinition[] queries, CancellationToken request)
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
        _producer = ProduceAsync(queries);
        // User-supplied cancel transport code must never run on the network reader or token callback.
        _control = serverCancellation ? Task.Run(ControlCancellationAsync) : Task.CompletedTask;
    }

    private async Task ProduceAsync(QueryDefinition[] queries)
    {
        bool admitted = false;
        try
        {
            await _pooled.Publication.WaitAsync(_request).ConfigureAwait(false);
            admitted = true;
            if (queries.Length == 1)
            {
                Task? send = null;
                Exception? failure = null;
                try { send = _batch.SendQueryAsync(queries[0].Sql, queries[0].Parameters).AsTask(); }
                catch (Exception ex) { failure = ex; }
                Task sync = _batch.SendSyncAsync().AsTask();
                // Both are queued before releasing the boundary, without waiting for FlushAsync.
                _pooled.Publication.Release();
                admitted = false;
                try { await Task.WhenAll(send ?? Task.CompletedTask, sync).ConfigureAwait(false); }
                catch (Exception ex) { failure ??= ex; }
                if (failure is not null) ExceptionDispatchInfo.Throw(failure);
            }
            else
            {
                try
                {
                    foreach (var query in queries)
                        await _batch.SendQueryAsync(query.Sql, query.Parameters).ConfigureAwait(false);
                }
                finally { await _batch.SendSyncAsync().ConfigureAwait(false); }
            }
        }
        finally
        {
            if (admitted) _pooled.Publication.Release();
            _batch.CompleteIfUnpublished();
        }
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
            try { await _batch.Completion.WaitAsync(recovery.Token).ConfigureAwait(false); }
            catch (MpgsqlServerException) { /* expected server cancellation/error, still a ReadyForQuery boundary */ }
        }
        catch { await _source.RetireAsync(_pooled).ConfigureAwait(false); }
    }

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
            if (waitForInputRelease) await _producer.ConfigureAwait(false);
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
            // Logical cancellation is prompt, but its admission slot survives until ReadyForQuery.
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
        TaskCompletionSource? signal = null;
        Task finishing;
        MpgsqlResultReader? reader;
        lock (_gate)
        {
            if (discard) _discard = true;
            reader = _reader;
            if (_finish is null)
            {
                signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _finish = signal.Task;
            }
            finishing = _finish;
        }
        if (discard)
        {
            _batch.BeginDiscard();
            Task idle = reader?.InvalidateFromOwner() ?? Task.CompletedTask;
            lock (_gate) _readerIdle = idle;
        }
        if (signal is not null) _ = CompleteFinishAsync(signal, discard);
        return new(ObserveFinishAsync(finishing));
    }

    private async Task ObserveFinishAsync(Task finishing)
    {
        try { await finishing.ConfigureAwait(false); }
        catch { if (Interlocked.Exchange(ref _finishErrorObserved, 1) == 0) throw; }
    }

    private async Task CompleteFinishAsync(TaskCompletionSource signal, bool discard)
    {
        try { await FinishCoreAsync(discard).ConfigureAwait(false); signal.TrySetResult(); }
        catch (Exception error) { signal.TrySetException(error); }
    }

    private async Task FinishProtocolAsync()
    {
        Exception? error = null;
        try { await _producer.ConfigureAwait(false); } catch (Exception ex) { error = ex; }
        try { await _batch.ObserveCompletionAsync().ConfigureAwait(false); } catch (Exception ex) { error ??= ex; }
        await _control.ConfigureAwait(false);
        if (error is not null && !_request.IsCancellationRequested) ExceptionDispatchInfo.Throw(error);
    }

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
