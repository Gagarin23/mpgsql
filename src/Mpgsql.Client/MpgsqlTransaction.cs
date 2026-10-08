using System.Data;
using System.Data.Common;

namespace Mpgsql;

public sealed class MpgsqlTransaction : DbTransaction
{
    private readonly MpgsqlConnection _connection;

    private bool _ended,
        _ending;

    internal MpgsqlTransaction(MpgsqlConnection connection, IsolationLevel isolation)
    {
        (_connection, IsolationLevel) = (connection, isolation);
    }
    internal bool IsActive => !_ended;
    public override IsolationLevel IsolationLevel { get; }

    protected override DbConnection? DbConnection => _ended ? null : _connection;
    public new MpgsqlConnection? Connection => (MpgsqlConnection?)DbConnection;
    public override bool SupportsSavepoints => false;
    public override void Commit()
    {
        throw new NotSupportedException("Use CommitAsync.");
    }
    public override void Rollback()
    {
        throw new NotSupportedException("Use RollbackAsync.");
    }
    public override Task CommitAsync(CancellationToken cancellationToken = default)
    {
        return EndAsync("COMMIT", cancellationToken);
    }
    public override Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        return EndAsync("ROLLBACK", cancellationToken);
    }
    private async Task EndAsync(string sql, CancellationToken token)
    {
        MpgsqlCommand command;
        Task<int> ending;
        lock (_connection.Gate)
        {
            if (_ended || _ending)
            {
                throw new InvalidOperationException("The transaction has already ended or is ending.");
            }
            _connection.CheckAvailable();
            _ending = true;
            command = _connection.CreateCommand(sql);
            command.Transaction = this;
            ending = command.ExecuteNonQueryAsync(token);
        }
        try
        {
            await using (command.ConfigureAwait(false))
            {
                await ending.ConfigureAwait(false);
            }
            lock (_connection.Gate)
            {
                _ended = true;
                _connection.CurrentTransaction = null;
            }
        }
        finally
        {
            lock (_connection.Gate)
            {
                _ending = false;
            }
        }
    }
    internal void Invalidate()
    {
        _ended = true;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_ended)
        {
            _connection.Abort();
            Invalidate();
        }
        base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
        if (!_ended)
        {
            await RollbackAsync()
                .ConfigureAwait(false);
        }
        GC.SuppressFinalize(this);
    }
    public override void Save(string savepointName)
    {
        throw new NotSupportedException("Savepoints are not supported.");
    }
    public override void Rollback(string savepointName)
    {
        throw new NotSupportedException("Savepoints are not supported.");
    }
    public override void Release(string savepointName)
    {
        throw new NotSupportedException("Savepoints are not supported.");
    }
}