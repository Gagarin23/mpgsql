using Mpgsql.Internal;
using Mpgsql.Protocol;

namespace Mpgsql;

/// <summary>Owns authenticated transports and schedules independent, pipelined query groups.</summary>
/// <remarks>The factory transfers exclusive ownership of idle protocol-3.0 UTF8 sessions. Completing
/// their pipe endpoints must release the transport. No connection/authentication or cleanup SQL is hidden
/// here. Dispose aborts transports and rejects further work; readers must still be disposed by callers.</remarks>
public sealed class MpgsqlDataSource : IAsyncDisposable
{
    private readonly Func<CancellationToken, ValueTask<MpgsqlMessageSession>> _factory;
    internal readonly Func<MpgsqlMessageSession, CancellationToken, ValueTask> SendCancelRequest;
    internal readonly MpgsqlDataSourceOptions Options;
    private readonly Lock _gate = new();
    private readonly List<PooledSession> _available = [];
    private readonly HashSet<PooledSession> _all = [];
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
    public async ValueTask<MpgsqlResultReader> ExecuteReaderAsync(string sql,
        ReadOnlyMemory<MpgsqlParameter> parameters = default, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = QueryPacket.GetByteCount(sql, parameters.Span);
        var pooled = await AcquireAsync(exclusive: false, cancellationToken).ConfigureAwait(false);
        QueryExecution execution;
        try { execution = new(this, pooled, null, [new(sql, parameters)], cancellationToken); }
        catch { await ReleaseRequestAsync(pooled).ConfigureAwait(false); throw; }
        return await execution.OpenReaderAsync(waitForInputRelease: true).ConfigureAwait(false);
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
    }

    private async ValueTask<PooledSession> AcquireAsync(bool exclusive, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Task wait;
            bool create = false;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                PooledSession? chosen = null;
                foreach (var candidate in _available)
                {
                    if (candidate.Retired || candidate.Leased || !candidate.Session.IsHealthy
                        || candidate.Session.LastTransactionStatus != TransactionStatus.Idle
                        || (exclusive ? candidate.Active != 0 : candidate.Active >= Options.MaxInFlightPerConnection)) continue;
                    if (chosen is null || candidate.Active < chosen.Active) chosen = candidate;
                    if (candidate.Active == 0) break;
                }
                if (chosen is not null)
                {
                    if (exclusive) chosen.Leased = true;
                    else chosen.Active++;
                    return chosen;
                }
                if (_available.Count + _creating < Options.MaxConnections)
                {
                    _creating++;
                    create = true;
                }
                wait = create ? Task.CompletedTask : (_changed ??= NewSignal()).Task;
            }
            if (!create)
            {
                await wait.WaitAsync(linked.Token).ConfigureAwait(false);
                continue;
            }

            PooledSession? created = null;
            MpgsqlMessageSession? returned = null;
            try
            {
                returned = await _factory(linked.Token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The session factory returned null.");
                returned.ClaimForDataSource(Options.MaxBufferedRowBytesPerConnection);
                created = new(returned);
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
            finally
            {
                lock (_gate) { _creating--; Signal(); }
            }
        }
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
            retire = !pooled.Session.IsHealthy || pooled.Session.LastTransactionStatus != TransactionStatus.Idle;
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
            retire = !pooled.Session.IsHealthy || pooled.Session.LastTransactionStatus != TransactionStatus.Idle;
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
