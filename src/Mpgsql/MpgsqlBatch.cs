using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Mpgsql.Internal;

namespace Mpgsql;

public sealed class MpgsqlBatch : DbBatch
{
    internal readonly Lock Gate = new Lock();
    private readonly Action _completed;
    private long[] _affectedRows = [];
    private MpgsqlConnection? _connection;

    private bool _disposed,
        _timeoutSet;

    private QueryExecution? _execution;
    private QueryDefinition[] _queries = [];
    private int _timeout;
    private MpgsqlTransaction? _transaction;
    public MpgsqlBatch()
    {
        _completed = Complete;
        BatchCommands = new MpgsqlBatchCommandCollection(this);
    }
    public MpgsqlBatch(MpgsqlConnection connection) : this()
    {
        Connection = connection;
        _timeout = connection.DefaultCommandTimeout;
    }
    internal bool IsBusy { get; private set; }
    protected override DbBatchCommandCollection DbBatchCommands => BatchCommands;
    public new MpgsqlBatchCommandCollection BatchCommands { get; }

    public override int Timeout
    {
        get => _timeout;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            lock (Gate)
            {
                CheckMutable();
                _timeout = value;
                _timeoutSet = true;
            }
        }
    }

    protected override DbConnection? DbConnection
    {
        get => _connection;
        set
        {
            lock (Gate)
            {
                CheckMutable();
                if (value is not null and not MpgsqlConnection)
                {
                    throw new ArgumentException("Expected MpgsqlConnection.");
                }
                if (!ReferenceEquals(_connection, value))
                {
                    for (var i = 0;
                         i < BatchCommands.Count;
                         i++)
                    {
                        if (BatchCommands[i].Statement is not null)
                        {
                            throw new InvalidOperationException("Unprepare before changing the connection.");
                        }
                    }
                }
                _connection = (MpgsqlConnection?)value;
                if (!_timeoutSet)
                {
                    _timeout = _connection?.DefaultCommandTimeout ?? 0;
                }
            }
        }
    }

    public new MpgsqlConnection? Connection
    {
        get => _connection;
        set => DbConnection = value;
    }

    protected override DbTransaction? DbTransaction
    {
        get => _transaction;
        set
        {
            lock (Gate)
            {
                CheckMutable();
                if (value is not null and not MpgsqlTransaction)
                {
                    throw new ArgumentException("Expected MpgsqlTransaction.");
                }
                _transaction = (MpgsqlTransaction?)value;
            }
        }
    }

    public new MpgsqlTransaction? Transaction
    {
        get => _transaction;
        set => DbTransaction = value;
    }

    internal void CheckDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
    internal void CheckMutable()
    {
        CheckDisposed();
        if (IsBusy)
        {
            throw new InvalidOperationException("The batch has an active execution or reader.");
        }
    }
    public override void Cancel()
    {
        lock (Gate)
        {
            _execution?.Cancel();
        }
    }
    protected override DbBatchCommand CreateDbBatchCommand()
    {
        return new MpgsqlBatchCommand();
    }
    public new MpgsqlBatchCommand CreateBatchCommand()
    {
        return (MpgsqlBatchCommand)CreateDbBatchCommand();
    }
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        throw new NotSupportedException("Use ExecuteReaderAsync.");
    }
    public override int ExecuteNonQuery()
    {
        throw new NotSupportedException("Use ExecuteNonQueryAsync.");
    }
    public override object? ExecuteScalar()
    {
        throw new NotSupportedException("Use ExecuteScalarAsync.");
    }
    public override void Prepare()
    {
        throw new NotSupportedException("Use PrepareAsync.");
    }
    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
    {
        return await ExecuteReaderValueTaskAsync(behavior, cancellationToken)
            .ConfigureAwait(false);
    }
    public async new Task<MpgsqlDataReader> ExecuteReaderAsync(CancellationToken cancellationToken = default)
    {
        return await ExecuteReaderValueTaskAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }
    public async new Task<MpgsqlDataReader> ExecuteReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken = default)
    {
        return await ExecuteReaderValueTaskAsync(behavior, cancellationToken)
            .ConfigureAwait(false);
    }
    private MpgsqlConnection ValidateConnection()
    {
        var connection = _connection ?? throw new InvalidOperationException("A batch requires an explicitly open connection.");
        if (_transaction is not null && (!ReferenceEquals(connection.CurrentTransaction, _transaction) || !_transaction.IsActive))
        {
            throw new InvalidOperationException("The transaction is no longer active on this connection.");
        }
        return connection;
    }
    private void Freeze()
    {
        CheckMutable();
        // Acquire every command gate before marking the batch busy; a concurrent parameter
        // setter finishes before the snapshot or observes the busy flag under its own gate.
        var entered = 0;
        try
        {
            for (;
                 entered < BatchCommands.Count;
                 entered++)
            {
                BatchCommands[entered]
                    .Gate.Enter();
            }
            IsBusy = true;
        }
        finally
        {
            while (entered != 0)
            {
                BatchCommands[--entered]
                    .Gate.Exit();
            }
        }
    }
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<MpgsqlDataReader> ExecuteReaderValueTaskAsync(CommandBehavior behavior = CommandBehavior.Default, CancellationToken cancellationToken = default)
    {
        MpgsqlDataReader.ValidateBehavior(behavior);
        var execution = BeginExecution(cancellationToken);
        try
        {
            var cursor = execution.Cursor;
            var hasRows = await cursor.InitializeAsync().ConfigureAwait(false);
            execution.CompleteReaderInitialization();
            var reader = new MpgsqlDataReader(cursor, execution, execution.Connection, behavior);
            reader.Initialize(hasRows);
            return reader;
        }
        catch (Exception error)
        {
            try { await execution.FinishAsync(true).ConfigureAwait(false); }
            catch { }
            ExceptionDispatchInfo.Throw(execution.Map(error));
            return null!;
        }
    }

    private QueryExecution BeginExecution(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MpgsqlConnection connection;
        QueryExecution execution;
        lock (Gate)
        {
            connection = ValidateConnection();
            Freeze();
            try
            {
                if (_queries.Length != BatchCommands.Count)
                {
                    _queries = new QueryDefinition[BatchCommands.Count];
                }
                if (_affectedRows.Length != BatchCommands.Count)
                {
                    _affectedRows = new long[BatchCommands.Count];
                }
                Array.Fill(_affectedRows, -1L);
                for (var i = 0;
                     i < _queries.Length;
                     i++)
                {
                    _queries[i] = BatchCommands[i]
                        .Snapshot();
                    BatchCommands[i].RecordsAffected64 = -1;
                }
                execution = connection.Start(default, _queries, cancellationToken, _timeout, _completed, out _execution, affectedRows: _affectedRows);
            }
            catch
            {
                IsBusy = false;
                Array.Clear(_queries);
                throw;
            }
        }
        return execution;
    }
    private void Complete()
    {
        lock (Gate)
        {
            if (_execution is not null && _affectedRows.Length == BatchCommands.Count)
            {
                for (var i = 0;
                     i < _affectedRows.Length;
                     i++)
                {
                    BatchCommands[i].RecordsAffected64 = _affectedRows[i];
                }
            }
            IsBusy = false;
            _execution = null;
            Array.Clear(_queries);
            if (_disposed)
            {
                ReleaseCommands();
            }
        }
    }
    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken = default)
    {
        return checked((int)await ExecuteNonQuery64Async(cancellationToken)
            .ConfigureAwait(false));
    }
    public async ValueTask<long> ExecuteNonQuery64Async(CancellationToken cancellationToken = default)
    {
        var execution = BeginExecution(cancellationToken);
        return await execution.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken = default)
    {
        var execution = BeginExecution(cancellationToken);
        return await execution.ExecuteScalarAsync().ConfigureAwait(false);
    }
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<MpgsqlScalarResult<T>> ExecuteScalarAsync<T>(CancellationToken cancellationToken = default)
    {
        var execution = BeginExecution(cancellationToken);
        return await execution.ExecuteScalarAsync<T>().ConfigureAwait(false);
    }
    public override Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        return PrepareCoreAsync(false, cancellationToken);
    }
    public ValueTask UnprepareAsync(CancellationToken cancellationToken = default)
    {
        return new ValueTask(PrepareCoreAsync(true, cancellationToken));
    }
    private async Task PrepareCoreAsync(bool close, CancellationToken token)
    {
        MpgsqlPreparedStatement[] statements;
        MpgsqlPreparedStatement?[] created;
        MpgsqlConnection connection;
        lock (Gate)
        {
            connection = ValidateConnection();
            Freeze();
            try
            {
                var pending = new List<MpgsqlPreparedStatement>();
                var queries = new QueryDefinition[BatchCommands.Count];
                if (!close)
                {
                    for (var i = 0;
                         i < queries.Length;
                         i++)
                    {
                        queries[i] = BatchCommands[i]
                            .Snapshot();
                    }
                }
                created = new MpgsqlPreparedStatement?[queries.Length];
                for (var i = 0;
                     i < BatchCommands.Count;
                     i++)
                {
                    var command = BatchCommands[i];
                    var query = queries[i];
                    if (close)
                    {
                        if (command.Statement is not null)
                        {
                            pending.Add(command.Statement);
                        }
                    }
                    else if (command.Statement is null)
                    {
                        var oids = new uint[query.Parameters.Length];
                        for (var j = 0;
                             j < oids.Length;
                             j++)
                        {
                            oids[j] = query.Parameters.Span[j].PostgresTypeOid;
                        }
                        created[i] = connection.Session.CreatePreparedStatement(query.Sql, oids);
                        pending.Add(created[i]!);
                    }
                }
                statements = [.. pending];
                _execution = connection.StartAdministration(statements, close, token, _timeout, static () => { });
            }
            catch
            {
                IsBusy = false;
                throw;
            }
        }
        try
        {
            await _execution
                .FinishAsync(false, true)
                .ConfigureAwait(false);
            if (close)
            {
                for (var i = 0;
                     i < BatchCommands.Count;
                     i++)
                {
                    BatchCommands[i]
                        .Statement?.Dispose();
                    BatchCommands[i].Statement = null;
                }
            }
        }
        finally
        {
            // A later Parse can fail after earlier statements were confirmed. Keep those
            // handles available for explicit Unprepare; failed/skipped handles stay local.
            if (!close)
            {
                for (var i = 0;
                     i < created.Length;
                     i++)
                {
                    if (created[i] is { } statement)
                    {
                        if (statement.Prepared.IsCompletedSuccessfully)
                        {
                            BatchCommands[i].Statement = statement;
                        }
                        else
                        {
                            statement.Dispose();
                        }
                    }
                }
            }
            Complete();
        }
    }
    private void ReleaseCommands()
    {
        BatchCommands.Release();
    }
    public override void Dispose()
    {
        QueryExecution? execution;
        lock (Gate)
        {
            _disposed = true;
            execution = _execution;
            if (!IsBusy)
            {
                ReleaseCommands();
            }
        }
        if (execution is not null)
        {
            _connection?.Abort();
            _ = ObserveAsync(execution);
        }
    }
    private static async Task ObserveAsync(QueryExecution execution)
    {
        try
        {
            await execution
                .FinishAsync(true)
                .ConfigureAwait(false);
        }
        catch { }
    }
    public override async ValueTask DisposeAsync()
    {
        QueryExecution? execution;
        lock (Gate)
        {
            _disposed = true;
            execution = _execution;
            if (!IsBusy)
            {
                ReleaseCommands();
            }
        }
        if (execution is not null)
        {
            await execution
                .FinishAsync(true)
                .ConfigureAwait(false);
        }
    }
}
