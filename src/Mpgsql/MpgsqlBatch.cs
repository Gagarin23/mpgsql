using Mpgsql.Internal;

namespace Mpgsql;

/// <summary>A single-use ordered group on an explicit connection, with one Sync/error boundary.</summary>
public sealed class MpgsqlBatch : IAsyncDisposable
{
    internal readonly MpgsqlConnection Connection;
    private bool _started;
    private bool _disposed;
    private QueryExecution? _execution;
    private Task? _dispose;
    public MpgsqlCommandCollection Commands { get; }

    internal MpgsqlBatch(MpgsqlConnection connection) { Connection = connection; Commands = new(this); }

    /// <summary>Creates a connection-bound command. Add it to Commands explicitly.</summary>
    public MpgsqlCommand CreateCommand(string sql)
    {
        lock (Connection.Gate) { CheckMutable(); return Connection.CreateCommand(sql); }
    }

    internal void CheckMutable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Connection.ThrowIfDisposed();
        if (_started) throw new InvalidOperationException("The batch has already started its single execution.");
    }

    internal QueryDefinition[] Start()
    {
        CheckMutable();
        _started = true;
        return [.. Commands.Select(c => c.Freeze())];
    }

    public async ValueTask<MpgsqlResultReader> ExecuteReaderAsync(CancellationToken cancellationToken = default)
    {
        QueryExecution execution;
        lock (Connection.Gate)
        {
            execution = Connection.StartBatch(this, cancellationToken);
            _execution = execution;
            foreach (var command in Commands) command.SetExecution(execution);
        }
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
        Exception? error = null;
        try { if (_execution is { } execution) await execution.FinishAsync(discard: true).ConfigureAwait(false); }
        catch (Exception ex) { error = ex; }
        foreach (var command in Commands)
        {
            try { await command.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { error ??= ex; }
        }
        lock (Connection.Gate) Commands.Release();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
    }
}
