using System.Data.Common;

namespace Mpgsql;

/// <summary>A pool of exclusively leased sessions. Closing a healthy idle lease sends no reset SQL.</summary>
public sealed class MpgsqlDataSource : DbDataSource
{
    private readonly HashSet<MpgsqlMessageSession> _all = [];
    private readonly Func<MpgsqlMessageSession, CancellationToken, ValueTask> _cancel;
    private readonly Func<CancellationToken, ValueTask<MpgsqlMessageSession>> _factory;
    private readonly Lock _gate = new Lock();
    private readonly Stack<MpgsqlMessageSession> _idle = new Stack<MpgsqlMessageSession>();
    private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
    private readonly SemaphoreSlim _slots;
    private int _creating;
    private TaskCompletionSource? _creationFinished;
    private Task? _dispose;
    private bool _disposed;

    public MpgsqlDataSource(string connectionString, MpgsqlDataSourceOptions? options = null)
    {
        var builder = new MpgsqlConnectionStringBuilder(connectionString);
        var settings = builder.ToSessionOptions();
        ConnectionString = builder.ConnectionString;
        Options = (options ?? new MpgsqlDataSourceOptions
        {
            MaxConnections = builder.MaxPoolSize
        }).CopyValidated();
        DefaultCommandTimeout = builder.CommandTimeout;
        OpenTimeout = settings.ConnectTimeout;
        _factory = token => MpgsqlMessageSession.OpenAdoAsync(settings, token);
        _cancel = static (session, token) => session.SendCancelRequestAsync(token);
        _slots = new SemaphoreSlim(Options.MaxConnections, Options.MaxConnections);
    }

    /// <summary>The factory transfers exclusive ownership of an authenticated, idle UTF8 session.</summary>
    public MpgsqlDataSource(
        Func<CancellationToken, ValueTask<MpgsqlMessageSession>> sessionFactory,
        Func<MpgsqlMessageSession, CancellationToken, ValueTask> sendCancelRequestAsync, MpgsqlDataSourceOptions? options = null
    )
    {
        ArgumentNullException.ThrowIfNull(sessionFactory);
        ArgumentNullException.ThrowIfNull(sendCancelRequestAsync);
        ConnectionString = "";
        Options = (options ?? new MpgsqlDataSourceOptions()).CopyValidated();
        _factory = sessionFactory;
        _cancel = sendCancelRequestAsync;
        _slots = new SemaphoreSlim(Options.MaxConnections, Options.MaxConnections);
    }
    internal MpgsqlDataSourceOptions Options { get; }
    internal int DefaultCommandTimeout { get; }
    internal TimeSpan OpenTimeout { get; } = TimeSpan.FromSeconds(15);
    public override string ConnectionString { get; }

    internal bool IsDisposed
    {
        get
        {
            lock (_gate)
            {
                return _disposed;
            }
        }
    }

    protected override DbConnection CreateDbConnection()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
        return new MpgsqlConnection(this);
    }
    public new MpgsqlConnection CreateConnection()
    {
        return (MpgsqlConnection)CreateDbConnection();
    }
    public async new ValueTask<MpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = CreateConnection();
        try
        {
            await connection
                .OpenAsync(cancellationToken)
                .ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection
                .DisposeAsync()
                .ConfigureAwait(false);
            throw;
        }
    }
    protected override async ValueTask<DbConnection> OpenDbConnectionAsync(CancellationToken cancellationToken = default)
    {
        return await OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
    }
    protected override DbConnection OpenDbConnection()
    {
        throw new NotSupportedException("Use OpenConnectionAsync.");
    }
    protected override DbCommand CreateDbCommand(string? commandText = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
        return new MpgsqlCommand(this, commandText ?? "");
    }
    public new MpgsqlCommand CreateCommand(string? commandText = null)
    {
        return (MpgsqlCommand)CreateDbCommand(commandText);
    }
    protected override DbBatch CreateDbBatch()
    {
        throw new NotSupportedException("Open a connection explicitly before creating a batch.");
    }

    internal async ValueTask<MpgsqlMessageSession> RentAsync(CancellationToken token)
    {
        using var linked = token.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token) : null;
        var lifetime = linked?.Token ?? _lifetime.Token;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
        await _slots
            .WaitAsync(lifetime)
            .ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                while (_idle.TryPop(out var session))
                {
                    if (session.IsIdleAndHealthy)
                    {
                        return session;
                    }
                    _all.Remove(session);
                    session.Abort(new IOException("The idle pool session is no longer reusable."));
                }
                _creating++;
            }
            MpgsqlMessageSession? created = null;
            var owned = false;
            try
            {
                created = await _factory(lifetime)
                    .ConfigureAwait(false) ?? throw new InvalidOperationException("The session factory returned null.");
                await created
                    .ClaimForAdoDataSourceAsync(Options.MaxBufferedRowBytesPerConnection, lifetime)
                    .ConfigureAwait(false);
                owned = true;
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    lifetime.ThrowIfCancellationRequested();
                    _all.Add(created);
                }
                _ = ObserveAsync(created);
                return created;
            }
            catch
            {
                if (created is not null && (owned || !created.IsClaimedForDataSource))
                {
                    await created
                        .DisposeAsync()
                        .ConfigureAwait(false);
                }
                throw;
            }
            finally
            {
                lock (_gate)
                {
                    if (--_creating == 0)
                    {
                        _creationFinished?.TrySetResult();
                    }
                }
            }
        }
        catch
        {
            _slots.Release();
            throw;
        }
    }

    private async Task ObserveAsync(MpgsqlMessageSession session)
    {
        try { await session.Completion.ConfigureAwait(false); }
        catch { }
        lock (_gate)
        {
            _all.Remove(session);
        }
        await session
            .DisposeAsync()
            .ConfigureAwait(false);
    }
    internal ValueTask SendCancelAsync(MpgsqlMessageSession session, CancellationToken token)
    {
        return _cancel(session, token);
    }
    internal async ValueTask ReturnAsync(MpgsqlMessageSession session)
    {
        bool close;
        lock (_gate)
        {
            close = _disposed || !session.IsIdleAndHealthy;
            if (!close)
            {
                _idle.Push(session);
            }
            else
            {
                _all.Remove(session);
            }
        }
        try
        {
            if (close)
            {
                await session
                    .DisposeAsync()
                    .ConfigureAwait(false);
            }
        }
        finally { _slots.Release(); }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                _lifetime.Cancel();
                foreach (var session in _all)
                {
                    session.Abort(new ObjectDisposedException(nameof(MpgsqlDataSource)));
                }
                _dispose = DisposeCoreAsync();
            }
        }
        base.Dispose(disposing);
    }
    protected override ValueTask DisposeAsyncCore()
    {
        lock (_gate)
        {
            if (_dispose is not null)
            {
                return new ValueTask(_dispose);
            }
            _disposed = true;
            _lifetime.Cancel();
            foreach (var session in _all)
            {
                session.Abort(new ObjectDisposedException(nameof(MpgsqlDataSource)));
            }
            return new ValueTask(_dispose = DisposeCoreAsync());
        }
    }
    private async Task DisposeCoreAsync()
    {
        Task? pending;
        lock (_gate)
        {
            pending = _creating == 0 ? null : (_creationFinished ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
        if (pending is not null)
        {
            await pending.ConfigureAwait(false);
        }
        MpgsqlMessageSession[] sessions;
        lock (_gate)
        {
            sessions = [.. _all];
            _all.Clear();
            _idle.Clear();
        }
        foreach (var session in sessions)
        {
            await session
                .DisposeAsync()
                .ConfigureAwait(false);
        }
    }
}
