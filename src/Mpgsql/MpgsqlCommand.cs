using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Mpgsql.Internal;

namespace Mpgsql;

public sealed class MpgsqlCommand : DbCommand
{
    private readonly Lock _gate = new Lock();
    private readonly MpgsqlDataSource? _source;

    private bool _busy,
        _disposed,
        _design,
        _timeoutSet;

    private MpgsqlConnection? _connection;
    private QueryExecution? _execution;
    private string _sql = "";
    private MpgsqlPreparedStatement? _statement;
    private int _timeout;
    private MpgsqlTransaction? _transaction;
    private UpdateRowSource _updated;
    public MpgsqlCommand()
    {
        Parameters = new MpgsqlParameterCollection(_gate, CheckMutable, () => _statement is not null);
    }
    public MpgsqlCommand(string sql, MpgsqlConnection? connection = null) : this()
    {
        CommandText = sql;
        Connection = connection;
        _timeout = connection?.DefaultCommandTimeout ?? 0;
    }
    internal MpgsqlCommand(MpgsqlDataSource source, string sql) : this(sql)
    {
        _source = source;
        _timeout = source.DefaultCommandTimeout;
    }

    [AllowNull]
    public override string CommandText
    {
        get => _sql;
        set
        {
            lock (_gate)
            {
                if (_sql == value)
                {
                    CheckMutable();
                    return;
                }
                CheckSignature();
                _sql = value ?? "";
            }
        }
    }

    public override int CommandTimeout
    {
        get => _timeout;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            lock (_gate)
            {
                CheckMutable();
                _timeout = value;
                _timeoutSet = true;
            }
        }
    }

    public override CommandType CommandType
    {
        get => CommandType.Text;
        set
        {
            lock (_gate)
            {
                CheckMutable();
                if (value != CommandType.Text)
                {
                    throw new NotSupportedException("Only CommandType.Text is supported.");
                }
            }
        }
    }

    protected override DbConnection? DbConnection
    {
        get => _connection;
        set
        {
            lock (_gate)
            {
                CheckMutable();
                if (value is not null and not MpgsqlConnection)
                {
                    throw new ArgumentException("Expected MpgsqlConnection.");
                }
                if (_source is not null)
                {
                    throw new InvalidOperationException("This command belongs to its data source.");
                }
                if (!ReferenceEquals(value, _connection))
                {
                    CheckSignature();
                    _connection = (MpgsqlConnection?)value;
                    if (!_timeoutSet)
                    {
                        _timeout = _connection?.DefaultCommandTimeout ?? 0;
                    }
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
            lock (_gate)
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

    protected override DbParameterCollection DbParameterCollection => Parameters;
    public new MpgsqlParameterCollection Parameters { get; }

    public override bool DesignTimeVisible
    {
        get => _design;
        set
        {
            lock (_gate)
            {
                CheckMutable();
                _design = value;
            }
        }
    }

    public override UpdateRowSource UpdatedRowSource
    {
        get => _updated;
        set
        {
            lock (_gate)
            {
                CheckMutable();
                if (value != UpdateRowSource.None)
                {
                    throw new NotSupportedException("Updated row sources are not supported.");
                }
                _updated = value;
            }
        }
    }

    private void CheckMutable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_busy)
        {
            throw new InvalidOperationException("The command has an active execution or reader.");
        }
    }
    private void CheckSignature()
    {
        CheckMutable();
        if (_statement is not null)
        {
            throw new InvalidOperationException("Call UnprepareAsync before changing the prepared signature.");
        }
    }
    protected override DbParameter CreateDbParameter()
    {
        return new MpgsqlParameter();
    }
    public new MpgsqlParameter CreateParameter()
    {
        return (MpgsqlParameter)CreateDbParameter();
    }
    public override void Cancel()
    {
        lock (_gate)
        {
            _execution?.Cancel();
        }
    }
    public override int ExecuteNonQuery()
    {
        throw new NotSupportedException("Use ExecuteNonQueryAsync.");
    }
    public override object? ExecuteScalar()
    {
        throw new NotSupportedException("Use ExecuteScalarAsync.");
    }
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        throw new NotSupportedException("Use ExecuteReaderAsync.");
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
        return await ExecuteReaderValueTaskAsync(CommandBehavior.Default, cancellationToken)
            .ConfigureAwait(false);
    }
    public async new Task<MpgsqlDataReader> ExecuteReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken = default)
    {
        return await ExecuteReaderValueTaskAsync(behavior, cancellationToken)
            .ConfigureAwait(false);
    }
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<MpgsqlDataReader> ExecuteReaderValueTaskAsync(CommandBehavior behavior = CommandBehavior.Default, CancellationToken cancellationToken = default)
    {
        MpgsqlDataReader.ValidateBehavior(behavior);
        cancellationToken.ThrowIfCancellationRequested();
        QueryDefinition query;
        MpgsqlConnection? connection;
        lock (_gate)
        {
            CheckMutable();
            query = new QueryDefinition(_sql, Parameters.Snapshot(), PreparedStatement: _statement);
            query = query with
            {
                EncodedSize = query.Measure()
            };
            connection = _connection;
            if (connection is null && _source is null)
            {
                throw new InvalidOperationException("Assign a connection first.");
            }
            _busy = true;
        }
        QueryExecution? execution = null;
        try
        {
            if (connection is null)
            {
                connection = await _source!
                    .OpenConnectionAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            lock (_gate)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(MpgsqlCommand));
                }
                ValidateTransaction(connection);
                execution = _execution = connection.Start(query, null, cancellationToken, _timeout, Complete, _source is not null);
            }
            var raw = await execution
                .OpenReaderAsync()
                .ConfigureAwait(false);
            var reader = new MpgsqlDataReader(raw, execution, connection, behavior);
            try
            {
                reader.Initialize
                (
                    await raw
                        .ReadAsync()
                        .ConfigureAwait(false)
                );
            }
            catch (Exception error)
            {
                try
                {
                    await execution
                        .FinishAsync(true)
                        .ConfigureAwait(false);
                }
                catch { }
                ExceptionDispatchInfo.Throw(execution.Map(error));
            }
            return reader;
        }
        catch
        {
            if (execution is null)
            {
                if (_source is not null && connection is not null)
                {
                    await connection
                        .DisposeAsync()
                        .ConfigureAwait(false);
                }
                Complete();
            }
            throw;
        }
    }
    private void ValidateTransaction(MpgsqlConnection connection)
    {
        if (_transaction is not null && (!ReferenceEquals(_transaction, connection.CurrentTransaction) || !_transaction.IsActive))
        {
            throw new InvalidOperationException("The transaction does not belong to this connection or has ended.");
        }
    }
    private void Complete()
    {
        lock (_gate)
        {
            _busy = false;
            _execution = null;
            if (_disposed)
            {
                Parameters.Release();
            }
        }
    }
    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        return checked((int)await ExecuteNonQuery64Async(cancellationToken)
            .ConfigureAwait(false));
    }
    public async ValueTask<long> ExecuteNonQuery64Async(CancellationToken cancellationToken = default)
    {
        await using var reader = await ExecuteReaderValueTaskAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        do
        {
            while (await reader
                       .ReadValueTaskAsync()
                       .ConfigureAwait(false)) { }
        }
        while (await reader
                   .NextResultValueTaskAsync()
                   .ConfigureAwait(false));
        return reader.RecordsAffected64;
    }
    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        await using var reader = await ExecuteReaderValueTaskAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        object? result = null;
        var selected = false;
        do
        {
            if (!selected && reader.IsRowSet)
            {
                selected = true;
                if (await reader
                        .ReadValueTaskAsync()
                        .ConfigureAwait(false) && reader.FieldCount != 0)
                {
                    result = reader.GetValue(0);
                }
            }
            while (await reader
                       .ReadValueTaskAsync()
                       .ConfigureAwait(false)) { }
        }
        while (await reader
                   .NextResultValueTaskAsync()
                   .ConfigureAwait(false));
        return result;
    }
    public async ValueTask<MpgsqlScalarResult<T>> ExecuteScalarAsync<T>(CancellationToken cancellationToken = default)
    {
        await using var reader = await ExecuteReaderValueTaskAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        MpgsqlScalarResult<T> result = default;
        var selected = false;
        do
        {
            if (!selected && reader.IsRowSet)
            {
                selected = true;
                if (await reader
                        .ReadValueTaskAsync()
                        .ConfigureAwait(false) && reader.FieldCount != 0)
                {
                    result = reader.IsDBNull(0) ? new MpgsqlScalarResult<T>(true, default) : new MpgsqlScalarResult<T>(false, reader.GetFieldValue<T>(0));
                }
            }
            while (await reader
                       .ReadValueTaskAsync()
                       .ConfigureAwait(false)) { }
        }
        while (await reader
                   .NextResultValueTaskAsync()
                   .ConfigureAwait(false));
        return result;
    }
    public override async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        MpgsqlPreparedStatement statement;
        MpgsqlConnection connection;
        lock (_gate)
        {
            CheckMutable();
            if (_statement is not null)
            {
                return;
            }
            connection = _connection ?? throw new InvalidOperationException("Preparation requires an explicitly open connection.");
            lock (connection.Gate)
            {
                connection.CheckAvailable();
            }
            ValidateTransaction(connection);
            var values = Parameters.Snapshot();
            new QueryDefinition(_sql, values).Measure();
            var oids = new uint[values.Length];
            for (var i = 0;
                 i < oids.Length;
                 i++)
            {
                oids[i] = values.Span[i].PostgresTypeOid;
            }
            statement = connection.Session.CreatePreparedStatement(_sql, oids);
            _busy = true;
        }
        try
        {
            lock (_gate)
            {
                _execution = connection.StartAdministration([statement], false, cancellationToken, _timeout, static () => { });
            }
            await _execution
                .FinishAsync(false, true)
                .ConfigureAwait(false);
        }
        finally
        {
            // ParseComplete remains authoritative if cancellation arrives before
            // ReadyForQuery. Keep the confirmed handle available for explicit Close.
            lock (_gate)
            {
                if (statement.Prepared.IsCompletedSuccessfully)
                {
                    _statement = statement;
                }
                else
                {
                    statement.Dispose();
                }
            }
            Complete();
        }
    }
    public async ValueTask UnprepareAsync(CancellationToken cancellationToken = default)
    {
        MpgsqlPreparedStatement statement;
        MpgsqlConnection connection;
        lock (_gate)
        {
            CheckMutable();
            if (_statement is null)
            {
                return;
            }
            statement = _statement;
            connection = _connection!;
            _busy = true;
        }
        try
        {
            lock (_gate)
            {
                _execution = connection.StartAdministration([statement], true, cancellationToken, _timeout, static () => { });
            }
            await _execution
                .FinishAsync(false, true)
                .ConfigureAwait(false);
            statement.Dispose();
            lock (_gate)
            {
                _statement = null;
            }
        }
        finally { Complete(); }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            QueryExecution? execution;
            lock (_gate)
            {
                _disposed = true;
                execution = _execution;
                if (!_busy)
                {
                    Parameters.Release();
                }
            }
            if (execution is not null)
            {
                execution.Abort();
                _ = ObserveDisposeAsync(execution);
            }
        }
        base.Dispose(disposing);
    }
    private static async Task ObserveDisposeAsync(QueryExecution execution)
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
        lock (_gate)
        {
            _disposed = true;
            execution = _execution;
            if (!_busy)
            {
                Parameters.Release();
            }
        }
        if (execution is not null)
        {
            await execution
                .FinishAsync(true)
                .ConfigureAwait(false);
        }
        GC.SuppressFinalize(this);
    }
}