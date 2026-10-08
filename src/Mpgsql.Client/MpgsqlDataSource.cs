using System.Runtime.CompilerServices;
using Mpgsql.Internal;
using Mpgsql.Protocol;

namespace Mpgsql;

/// <summary>Owns authenticated transports and schedules independent, pipelined query groups.</summary>
/// <remarks>The factory transfers exclusive ownership of idle protocol-3.0 UTF8 sessions. Completing
/// their pipe endpoints must release the transport. No connection/authentication or cleanup SQL is hidden
/// here. Dispose aborts transports and rejects further work; readers must still be disposed by callers.</remarks>
/// <remarks>SyncGroupSize greater than one explicitly shares transaction/error boundaries between
/// direct requests. Explicitly leased connections retain independent execution boundaries.</remarks>
public sealed class MpgsqlDataSource : IAsyncDisposable
{
    private readonly Func<CancellationToken, ValueTask<MpgsqlMessageSession>> _factory;
    internal readonly Func<MpgsqlMessageSession, CancellationToken, ValueTask> SendCancelRequest;
    internal readonly MpgsqlDataSourceOptions Options;
    private readonly Lock _gate = new();
    private readonly List<PooledSession> _available = [];
    private readonly HashSet<PooledSession> _all = [];
    private readonly LinkedList<SessionWaiter> _waiters = new();
    private readonly CancellationTokenSource _lifetime = new();
    private TaskCompletionSource? _changed;
    private int _creating;
    private bool _disposed;
    private Task? _dispose;

    public MpgsqlDataSource(
        Func<CancellationToken, ValueTask<MpgsqlMessageSession>> sessionFactory,
        Func<MpgsqlMessageSession, CancellationToken, ValueTask> sendCancelRequestAsync,
        MpgsqlDataSourceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sessionFactory);
        ArgumentNullException.ThrowIfNull(sendCancelRequestAsync);
        _factory = sessionFactory;
        SendCancelRequest = sendCancelRequestAsync;
        Options = (options ?? new()).CopyValidated();
    }

    internal CancellationToken LifetimeToken => _lifetime.Token;

    public async ValueTask<MpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        => new(this, await AcquireAsync(exclusive: true, cancellationToken).ConfigureAwait(false));

    /// <summary>Input memory is borrowed until this method completes. The reader owns the request slot
    /// until it is fully consumed or disposed. Slow readers apply transport-wide backpressure.</summary>
    public ValueTask<MpgsqlResultReader> ExecuteReaderAsync(string sql,
        ReadOnlyMemory<MpgsqlParameter> parameters = default, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            int size = QueryPacket.GetByteCount(sql, parameters.Span);
            var query = new QueryDefinition(sql, parameters, size);
            var admission = AcquireAsync(exclusive: false, cancellationToken);
            // A reserved slot needs no forwarding async machine around OpenReaderAsync.
            return admission.IsCompletedSuccessfully
                ? OpenReservedReader(admission.Result, query, cancellationToken)
                : OpenAdmittedReaderAsync(admission, query, cancellationToken);
        }
        catch (Exception error) { return QueryExecution.ReaderFailureAsync(error); }
    }

    private ValueTask<MpgsqlResultReader> OpenReservedReader(PooledSession pooled,
        QueryDefinition query, CancellationToken token)
    {
        try { return new QueryExecution(this, pooled, query, token).OpenReaderAsync(waitForInputRelease: true); }
        catch (Exception error) { return ReleaseFailedReservationAsync(pooled, error); }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<MpgsqlResultReader> OpenAdmittedReaderAsync(ValueTask<PooledSession> admission,
        QueryDefinition query, CancellationToken token)
        => await OpenReservedReader(await admission.ConfigureAwait(false), query, token).ConfigureAwait(false);

    private async ValueTask<MpgsqlResultReader> ReleaseFailedReservationAsync(PooledSession pooled, Exception error)
    {
        await ReleaseRequestAsync(pooled).ConfigureAwait(false);
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
        return null!;
    }

    public async ValueTask<MpgsqlScalarResult<T>> ExecuteScalarAsync<T>(string sql,
        ReadOnlyMemory<MpgsqlParameter> parameters = default, CancellationToken cancellationToken = default)
        => await ResultConsumption.ScalarAsync<T>(await ExecuteReaderAsync(sql, parameters, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

    public async ValueTask<long> ExecuteNonQueryAsync(string sql,
        ReadOnlyMemory<MpgsqlParameter> parameters = default, CancellationToken cancellationToken = default)
        => await ResultConsumption.NonQueryAsync(await ExecuteReaderAsync(sql, parameters, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void Signal()
    {
        var previous = _changed;
        _changed = null;
        previous?.TrySetResult();
        while (_waiters.First is { } node)
        {
            var waiter = node.Value;
            if (_disposed || waiter.Token.IsCancellationRequested)
            {
                _waiters.RemoveFirst();
                waiter.Node = null;
                if (waiter.Token.IsCancellationRequested) waiter.TrySetCanceled();
                else waiter.TrySetException(new ObjectDisposedException(nameof(MpgsqlDataSource)));
                continue;
            }
            if (TryReserve(waiter.Exclusive) is { } pooled)
            {
                _waiters.RemoveFirst();
                waiter.Node = null;
                waiter.TrySetResult(pooled);
            }
            else if (_available.Count + _creating < Options.MaxConnections)
            {
                _waiters.RemoveFirst();
                waiter.Node = null;
                _creating++;
                // A user factory never runs under the admission lock or on the receive loop.
                _ = Task.Run(() => CreateForWaiterAsync(waiter));
            }
            else break;
        }
    }

    // Called with the data source gate held. Reserve before waking a waiter so newcomers cannot steal it.
    private PooledSession? TryReserve(bool exclusive)
    {
        PooledSession? chosen = null;
        foreach (var candidate in _available)
        {
            if (candidate.Retired || candidate.Leased
                || (exclusive ? candidate.Active != 0 : candidate.Active >= Options.MaxInFlightPerConnection)
                || !candidate.Session.IsIdleAndHealthy) continue;
            if (chosen is null || candidate.Active < chosen.Active) chosen = candidate;
            if (candidate.Active == 0) break;
        }
        if (chosen is null) return null;
        if (exclusive) chosen.Leased = true;
        else chosen.Active++;
        return chosen;
    }

    private ValueTask<PooledSession> AcquireAsync(bool exclusive, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        SessionWaiter? waiter = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_waiters.Count == 0)
            {
                if (TryReserve(exclusive) is { } pooled) return new(pooled);
                if (_available.Count + _creating < Options.MaxConnections) _creating++;
                else waiter = new(this, exclusive, token);
            }
            else waiter = new(this, exclusive, token);
            if (waiter is not null)
            {
                waiter.Node = _waiters.AddLast(waiter);
                Signal();
            }
        }
        return waiter is null ? CreateSessionAsync(exclusive, token) : WaitForSessionAsync(waiter);
    }

    private static ValueTask<PooledSession> WaitForSessionAsync(SessionWaiter waiter)
        => waiter.Token.CanBeCanceled ? WaitForCancelableSessionAsync(waiter) : waiter.WaitAsync();

    private static async ValueTask<PooledSession> WaitForCancelableSessionAsync(SessionWaiter waiter)
    {
        using var registration = waiter.Token.UnsafeRegister(static state =>
        {
            var waiting = (SessionWaiter)state!;
            waiting.Source.CancelWaiter(waiting);
        }, waiter);
        return await waiter.WaitAsync().ConfigureAwait(false);
    }

    private void CancelWaiter(SessionWaiter waiter)
    {
        lock (_gate)
        {
            if (waiter.Node is not { } node) return; // A handed-off slot is now owned by the caller.
            _waiters.Remove(node);
            waiter.Node = null;
            waiter.TrySetCanceled();
            Signal();
        }
    }

    private async Task CreateForWaiterAsync(SessionWaiter waiter)
    {
        try { waiter.TrySetResult(await CreateSessionAsync(waiter.Exclusive, waiter.Token).ConfigureAwait(false)); }
        catch (OperationCanceledException) when (waiter.Token.IsCancellationRequested) { waiter.TrySetCanceled(); }
        catch (Exception error) { waiter.TrySetException(error); }
    }

    private async ValueTask<PooledSession> CreateSessionAsync(bool exclusive, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        PooledSession? created = null;
        MpgsqlMessageSession? returned = null;
        try
        {
            returned = await _factory(linked.Token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The session factory returned null.");
            returned.ClaimForDataSource(Options.MaxBufferedRowBytesPerConnection);
            created = new(returned, Options);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                token.ThrowIfCancellationRequested();
                _available.Add(created);
                _all.Add(created);
                if (exclusive) created.Leased = true;
                else created.Active = 1;
            }
            _ = MonitorAsync(created);
            return created;
        }
        catch
        {
            if (created is not null) await created.DisposeAsync().ConfigureAwait(false);
            else if (returned is not null && !returned.IsClaimedForDataSource)
                await returned.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        finally { lock (_gate) { _creating--; Signal(); } }
    }

    private async Task MonitorAsync(PooledSession pooled)
    {
        try { await pooled.Session.Completion.ConfigureAwait(false); }
        catch { /* session failure is also delivered to its outstanding operations */ }
        await RetireAsync(pooled).ConfigureAwait(false);
    }

    internal async ValueTask ReleaseRequestAsync(PooledSession pooled)
    {
        bool retire;
        lock (_gate)
        {
            pooled.Active--;
            retire = !pooled.Session.IsIdleAndHealthy;
            Signal();
        }
        if (retire) await RetireAsync(pooled).ConfigureAwait(false);
    }

    internal async ValueTask ReturnConnectionAsync(PooledSession pooled)
    {
        bool retire;
        lock (_gate)
        {
            pooled.Leased = false;
            retire = !pooled.Session.IsIdleAndHealthy;
            Signal();
        }
        if (retire) await RetireAsync(pooled).ConfigureAwait(false);
    }

    internal async Task RetireAsync(PooledSession pooled)
    {
        lock (_gate)
        {
            pooled.Retired = true;
            _available.Remove(pooled);
            Signal();
        }
        await pooled.DisposeAsync().ConfigureAwait(false);
        lock (_gate) _all.Remove(pooled);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_dispose is not null) return new(_dispose);
            _disposed = true;
            _lifetime.Cancel();
            Signal();
            return new(_dispose = DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                if (_creating == 0) break;
                wait = (_changed ??= NewSignal()).Task;
            }
            await wait.ConfigureAwait(false);
        }
        PooledSession[] sessions;
        lock (_gate) { sessions = [.. _all]; _available.Clear(); }
        await Task.WhenAll(sessions.Select(RetireAsync)).ConfigureAwait(false);
        // Keep the CTS alive: outstanding owners can still inspect the cancelled lifetime token.
    }
}
