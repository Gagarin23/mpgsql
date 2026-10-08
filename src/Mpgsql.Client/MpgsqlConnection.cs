using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Transactions;
using Mpgsql.Internal;
using IsolationLevel = System.Data.IsolationLevel;

namespace Mpgsql;

public sealed class MpgsqlConnection : DbConnection
{
    internal readonly Lock Gate = new Lock();
    private readonly MpgsqlDataSource? _source;
    internal MpgsqlTransaction? CurrentTransaction;
    private QueryExecution? _active;
    private string _connectionString = "";
    private bool _disposed, _closing;
    private Task? _open, _close;
    private CancellationTokenSource? _opening;
    private MpgsqlMessageSession? _session;
    private ConnectionState _state;
    private MpgsqlTypeMapper? _typeMapper;
    public MpgsqlConnection() { }
    public MpgsqlConnection(string connectionString)
    {
        ConnectionString = connectionString;
    }
    internal MpgsqlConnection(MpgsqlDataSource source)
    {
        _source = source;
        _connectionString = source.ConnectionString;
    }

    public MpgsqlTypeMapper? TypeMapper
    {
        get => _source?.Options.TypeMapper ?? _typeMapper;
        set
        {
            lock (Gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_source is not null || _state != ConnectionState.Closed || _closing)
                {
                    throw new InvalidOperationException("Configure type mappings before opening a standalone connection, or on the data-source options.");
                }
                _typeMapper = value?.Snapshot();
            }
        }
    }

    internal MpgsqlMessageSession Session => _session ?? throw new InvalidOperationException("Open the connection first.");
    internal TimeSpan RecoveryTimeout => _source?.Options.RecoveryTimeout ?? TimeSpan.FromSeconds(5);
    internal int DefaultCommandTimeout => _source?.DefaultCommandTimeout ?? (_connectionString.Length == 0 ? 0 : new MpgsqlConnectionStringBuilder(_connectionString).CommandTimeout);

    [AllowNull]
    public override string ConnectionString
    {
        get => _connectionString;
        set
        {
            lock (Gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_state != ConnectionState.Closed || _closing)
                {
                    throw new InvalidOperationException("Close the connection before changing its settings.");
                }
                if (_source is not null && value != _source.ConnectionString)
                {
                    throw new InvalidOperationException("A data-source connection retains its source settings.");
                }
                _connectionString = value ?? "";
            }
        }
    }

    public override string Database => _connectionString.Length == 0 ? "" : new MpgsqlConnectionStringBuilder(_connectionString).Database;
    public override string DataSource => _connectionString.Length == 0 ? "" : new MpgsqlConnectionStringBuilder(_connectionString).Host;
    public override string ServerVersion => Session.TryGetParameter("server_version", out var version) ? version! : "";

    public override ConnectionState State
    {
        get
        {
            lock (Gate)
            {
                return _state;
            }
        }
    }

    public override int ConnectionTimeout => _source is not null ? (int)_source.OpenTimeout.TotalSeconds : _connectionString.Length == 0 ? 15 : new MpgsqlConnectionStringBuilder(_connectionString).Timeout;
    protected override DbProviderFactory DbProviderFactory => MpgsqlFactory.Instance;
    public override bool CanCreateBatch => true;
    public override void Open()
    {
        throw new NotSupportedException("Use OpenAsync.");
    }
    public override Task OpenAsync(CancellationToken cancellationToken)
    {
        lock (Gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state != ConnectionState.Closed || _closing || _close is {IsCompleted: false})
            {
                throw new InvalidOperationException("The connection is already open or closing.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            if ((uint)ConnectionTimeout > (uint.MaxValue - 1) / 1000)
            {
                throw new ArgumentOutOfRangeException(nameof(ConnectionTimeout));
            }
            _close = null;
            _opening = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (ConnectionTimeout != 0)
            {
                _opening.CancelAfter(TimeSpan.FromSeconds(ConnectionTimeout));
            }
            ChangeState(ConnectionState.Connecting);
            return _open = OpenCoreAsync(_opening, cancellationToken);
        }
    }
    private async Task OpenCoreAsync(CancellationTokenSource opening, CancellationToken caller)
    {
        MpgsqlMessageSession? session = null;
        try
        {
            session = _source is not null
                ? await _source.RentAsync(opening.Token).ConfigureAwait(false)
                : await MpgsqlMessageSession.OpenAsync(new MpgsqlConnectionStringBuilder(_connectionString).ToSessionOptions(), opening.Token).ConfigureAwait(false);
            lock (Gate)
            {
                opening.Token.ThrowIfCancellationRequested();
                _session = session;
                ChangeState(ConnectionState.Open);
            }
            _ = ObserveAsync(session);
        }
        catch (Exception error)
        {
            if (session is not null)
            {
                if (_source is not null)
                {
                    await _source.ReturnAsync(session).ConfigureAwait(false);
                }
                else
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                }
            }
            lock (Gate)
            {
                ChangeState(ConnectionState.Closed);
            }
            if (error is OperationCanceledException && !caller.IsCancellationRequested && !_closing && !_disposed && _source?.IsDisposed != true)
            {
                throw new MpgsqlException("Opening the PostgreSQL connection timed out.", new TimeoutException(null, error));
            }
            ExceptionDispatchInfo.Throw(MpgsqlException.Map(error));
        }
        finally
        {
            lock (Gate)
            {
                if (ReferenceEquals(_opening, opening))
                {
                    _opening = null;
                }
            }
            opening.Dispose();
        }
    }
    private async Task ObserveAsync(MpgsqlMessageSession session)
    {
        try { await session.Completion.ConfigureAwait(false); }
        catch { }
        lock (Gate)
        {
            if (ReferenceEquals(_session, session) && _state == ConnectionState.Open)
            {
                ChangeState(ConnectionState.Broken);
            }
        }
        if (_source is null)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }
    private void ChangeState(ConnectionState next)
    {
        var previous = _state;
        _state = next;
        if (previous != next)
        {
            OnStateChange(new StateChangeEventArgs(previous, next));
        }
    }
    internal void CheckAvailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_closing || _state != ConnectionState.Open || !Session.IsHealthy)
        {
            throw new InvalidOperationException("The connection is not open and healthy.");
        }
        if (_active is not null)
        {
            throw new InvalidOperationException("The connection already has an active execution or reader.");
        }
    }
    internal QueryExecution Start(QueryDefinition single, QueryDefinition[]? queries,
        CancellationToken token, int timeout,
        Action completed, bool ownsConnection = false,
        long[]? affectedRows = null)
    {
        lock (Gate)
        {
            CheckAvailable();
            var execution = _active = new QueryExecution(this, token, timeout, completed, ownsConnection, affectedRows);
            execution.Publish(single, queries);
            return execution;
        }
    }
    internal QueryExecution StartAdministration(MpgsqlPreparedStatement[] statements, bool close,
        CancellationToken token, int timeout,
        Action completed)
    {
        lock (Gate)
        {
            CheckAvailable();
            var execution = _active = new QueryExecution(this, token, timeout, completed, false);
            execution.PublishAdministration(statements, close);
            return execution;
        }
    }
    internal void ExecutionCompleted(QueryExecution execution)
    {
        lock (Gate)
        {
            if (ReferenceEquals(_active, execution))
            {
                _active = null;
            }
        }
    }
    internal ValueTask SendCancelAsync(MpgsqlMessageSession session, CancellationToken token)
    {
        return _source is not null ? _source.SendCancelAsync(session, token) : session.SendCancelRequestAsync(token);
    }
    internal void Abort()
    {
        lock (Gate)
        {
            _session?.Abort(new IOException("The active connection was closed."));
            if (_state == ConnectionState.Open)
            {
                ChangeState(ConnectionState.Broken);
            }
        }
    }
    protected override DbCommand CreateDbCommand()
    {
        return new MpgsqlCommand("", this);
    }
    public new MpgsqlCommand CreateCommand()
    {
        return (MpgsqlCommand)CreateDbCommand();
    }
    public MpgsqlCommand CreateCommand(string sql)
    {
        return new MpgsqlCommand(sql, this);
    }
    protected override DbBatch CreateDbBatch()
    {
        return new MpgsqlBatch(this);
    }
    public new MpgsqlBatch CreateBatch()
    {
        return (MpgsqlBatch)CreateDbBatch();
    }
    public override void ChangeDatabase(string databaseName)
    {
        throw new NotSupportedException("Create a connection to the desired database.");
    }
    public override Task ChangeDatabaseAsync(string databaseName, CancellationToken cancellationToken = default)
    {
        return Task.FromException(new NotSupportedException("Create a connection to the desired database."));
    }
    public override void EnlistTransaction(Transaction? transaction)
    {
        if (transaction is not null)
        {
            throw new NotSupportedException("Ambient transactions are not supported.");
        }
    }
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        throw new NotSupportedException("Use BeginTransactionAsync.");
    }
    protected override async ValueTask<DbTransaction> BeginDbTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken)
    {
        var sql = isolationLevel switch
        {
            IsolationLevel.Unspecified or IsolationLevel.ReadCommitted => "BEGIN ISOLATION LEVEL READ COMMITTED",
            IsolationLevel.ReadUncommitted                             => "BEGIN ISOLATION LEVEL READ UNCOMMITTED",
            IsolationLevel.RepeatableRead                              => "BEGIN ISOLATION LEVEL REPEATABLE READ", IsolationLevel.Serializable => "BEGIN ISOLATION LEVEL SERIALIZABLE",
            _                                                          => throw new NotSupportedException($"Isolation level {isolationLevel} is not supported.")
        };
        MpgsqlTransaction transaction;
        MpgsqlCommand command;
        Task<int> beginning;
        lock (Gate)
        {
            CheckAvailable();
            if (CurrentTransaction is not null)
            {
                throw new InvalidOperationException("A transaction is already active.");
            }
            transaction = CurrentTransaction = new MpgsqlTransaction(this, isolationLevel == IsolationLevel.Unspecified ? IsolationLevel.ReadCommitted : isolationLevel);
            command = CreateCommand(sql);
            beginning = command.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (command.ConfigureAwait(false))
        {
            try
            {
                await beginning.ConfigureAwait(false);
                return transaction;
            }
            catch
            {
                lock (Gate)
                {
                    transaction.Invalidate();
                    if (ReferenceEquals(CurrentTransaction, transaction))
                    {
                        CurrentTransaction = null;
                    }
                }
                throw;
            }
        }
    }
    public async ValueTask ClearSessionStateAsync(MpgsqlSessionCleanup flags, CancellationToken cancellationToken = default)
    {
        const MpgsqlSessionCleanup known = MpgsqlSessionCleanup.Settings | MpgsqlSessionCleanup.ListenSubscriptions | MpgsqlSessionCleanup.AdvisoryLocks | MpgsqlSessionCleanup.Cursors | MpgsqlSessionCleanup.TemporaryObjects;
        if ((flags & ~known) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(flags));
        }
        cancellationToken.ThrowIfCancellationRequested();
        lock (Gate)
        {
            CheckAvailable();
        }
        if (flags == MpgsqlSessionCleanup.None)
        {
            return;
        }
        await using var batch = CreateBatch();
        if ((flags & MpgsqlSessionCleanup.Settings) != 0)
        {
            batch.BatchCommands.Add(new MpgsqlBatchCommand("RESET ALL"));
        }
        if ((flags & MpgsqlSessionCleanup.ListenSubscriptions) != 0)
        {
            batch.BatchCommands.Add(new MpgsqlBatchCommand("UNLISTEN *"));
        }
        if ((flags & MpgsqlSessionCleanup.AdvisoryLocks) != 0)
        {
            batch.BatchCommands.Add(new MpgsqlBatchCommand("SELECT pg_catalog.pg_advisory_unlock_all()"));
        }
        if ((flags & MpgsqlSessionCleanup.Cursors) != 0)
        {
            batch.BatchCommands.Add(new MpgsqlBatchCommand("CLOSE ALL"));
        }
        if ((flags & MpgsqlSessionCleanup.TemporaryObjects) != 0)
        {
            batch.BatchCommands.Add(new MpgsqlBatchCommand("DISCARD TEMP"));
        }
        await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
    private TaskCompletionSource? ReserveClose()
    {
        if (_close is not null)
        {
            return null;
        }
        _closing = true;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _close = completion.Task;
        return completion;
    }
    private async Task CompleteCloseAsync(TaskCompletionSource completion)
    {
        try
        {
            await CloseCoreAsync().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception error) { completion.TrySetException(error); }
    }
    public override void Close()
    {
        TaskCompletionSource? completion;
        lock (Gate)
        {
            completion = ReserveClose();
            _opening?.Cancel();
            if (_active is not null)
            {
                Abort();
            }
        }
        if (completion is not null)
        {
            _ = CompleteCloseAsync(completion);
        }
    }
    public override Task CloseAsync()
    {
        TaskCompletionSource? completion;
        Task task;
        lock (Gate)
        {
            completion = ReserveClose();
            _opening?.Cancel();
            task = _close!;
        }
        if (completion is not null)
        {
            _ = CompleteCloseAsync(completion);
        }
        return task;
    }
    internal Task CloseOwnedLeaseAsync()
    {
        TaskCompletionSource? completion;
        lock (Gate)
        {
            completion = ReserveClose();
        }
        // Connection.CloseAsync may be waiting for this execution; never await that same close.
        if (completion is null)
        {
            return Task.CompletedTask;
        }
        _ = CompleteCloseAsync(completion);
        return completion.Task;
    }
    private async Task CloseCoreAsync()
    {
        if (_open is {IsCompleted: false} pending)
        {
            try { await pending.ConfigureAwait(false); }
            catch { }
        }
        MpgsqlMessageSession? session;
        QueryExecution? active;
        lock (Gate)
        {
            session = _session;
            active = _active;
            _session = null;
            ChangeState(ConnectionState.Closed);
        }
        try
        {
            if (active is not null)
            {
                await active.FinishAsync(true).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                CurrentTransaction?.Invalidate();
                CurrentTransaction = null;
                if (session is not null)
                {
                    if (_source is not null)
                    {
                        await _source.ReturnAsync(session).ConfigureAwait(false);
                    }
                    else
                    {
                        await session.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                lock (Gate)
                {
                    _closing = false;
                }
            }
        }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Close();
            lock (Gate)
            {
                _disposed = true;
            }
        }
        base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
        lock (Gate)
        {
            _disposed = true;
        }
        await CloseAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}