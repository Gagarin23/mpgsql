using Mpgsql.Internal;

namespace Mpgsql;

/// <summary>A single-use connection-bound command. Parameter payload memory is borrowed throughout execution.</summary>
public sealed class MpgsqlCommand : IAsyncDisposable
{
    internal readonly MpgsqlConnection Connection;
    internal MpgsqlBatch? Batch;
    private string _text;
    private bool _started;
    private bool _disposed;
    private QueryExecution? _execution;
    private Task? _dispose;
    public MpgsqlParameterCollection Parameters { get; }

    internal MpgsqlCommand(MpgsqlConnection connection, string sql)
    {
        Connection = connection;
        _text = sql;
        Parameters = new(this);
    }

    public string CommandText
    {
        get { lock (Connection.Gate) return _text; }
        set { ArgumentNullException.ThrowIfNull(value); lock (Connection.Gate) { CheckMutable(); _text = value; } }
    }

    internal void CheckMutable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Connection.ThrowIfDisposed();
        if (_started) throw new InvalidOperationException("The command has already started its single execution.");
        Batch?.CheckMutable();
    }

    internal QueryDefinition StartStandalone()
    {
        CheckMutable();
        if (Batch is not null) throw new InvalidOperationException("Execute the owning batch instead of its command.");
        return Freeze();
    }

    internal QueryDefinition Freeze()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) throw new InvalidOperationException("The command has already started its single execution.");
        _started = true;
        var parameters = Parameters.ToArray();
        _ = QueryPacket.GetByteCount(_text, parameters);
        return new(_text, parameters);
    }

    internal void SetExecution(QueryExecution execution) => _execution = execution;

    public async ValueTask<MpgsqlResultReader> ExecuteReaderAsync(CancellationToken cancellationToken = default)
    {
        QueryExecution execution;
        lock (Connection.Gate) { execution = Connection.StartCommand(this, cancellationToken); _execution = execution; }
        return await execution.OpenReaderAsync().ConfigureAwait(false);
    }

    public async ValueTask<MpgsqlScalarResult<T>> ExecuteScalarAsync<T>(CancellationToken cancellationToken = default)
        => await ResultConsumption.ScalarAsync<T>(await ExecuteReaderAsync(cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

    public async ValueTask<long> ExecuteNonQueryAsync(CancellationToken cancellationToken = default)
        => await ResultConsumption.NonQueryAsync(await ExecuteReaderAsync(cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

    public ValueTask DisposeAsync()
    {
        lock (Connection.Gate)
        {
            if (_dispose is not null) return new(_dispose);
            _disposed = true;
            return new(_dispose = DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        try { if (_execution is { } execution) await execution.FinishAsync(discard: true).ConfigureAwait(false); }
        finally { lock (Connection.Gate) { Parameters.Release(); _text = ""; } }
    }
}
