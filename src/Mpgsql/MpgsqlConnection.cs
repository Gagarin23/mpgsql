using Mpgsql.Internal;

namespace Mpgsql;

/// <summary>An exclusive lease of a client transport, with at most one active upper-level execution.</summary>
/// <remarks>Backend affinity through a transaction pooler is only as strong as that pooler's transaction
/// affinity. Returning a healthy idle lease sends no reset or rollback SQL.</remarks>
public sealed class MpgsqlConnection : IAsyncDisposable
{
    internal readonly Lock Gate = new();
    internal readonly MpgsqlDataSource Source;
    internal readonly PooledSession Pooled;
    private QueryExecution? _active;
    private bool _disposed;
    private Task? _dispose;

    internal MpgsqlConnection(MpgsqlDataSource source, PooledSession pooled) => (Source, Pooled) = (source, pooled);

    public MpgsqlCommand CreateCommand(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        lock (Gate) { ThrowIfDisposed(); return new(this, sql); }
    }

    public MpgsqlBatch CreateBatch()
    {
        lock (Gate) { ThrowIfDisposed(); return new(this); }
    }

    internal void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Pooled.Session.IsHealthy) throw new IOException("The connection transport is closed.");
    }

    private void CheckAvailable()
    {
        ThrowIfDisposed();
        if (_active is not null) throw new InvalidOperationException("The connection already has an active execution or reader.");
    }

    internal QueryExecution StartCommand(MpgsqlCommand command, CancellationToken token)
    {
        lock (Gate)
        {
            CheckAvailable();
            return _active = new(Source, Pooled, this, [command.StartStandalone()], token);
        }
    }

    internal QueryExecution StartBatch(MpgsqlBatch batch, CancellationToken token)
    {
        lock (Gate)
        {
            CheckAvailable();
            return _active = new(Source, Pooled, this, batch.Start(), token);
        }
    }

    internal void ExecutionCompleted(QueryExecution execution)
    {
        lock (Gate) { if (_active == execution) _active = null; }
    }

    /// <summary>Runs only selected cleanup operations. Prepared statements are not deallocated.
    /// The caller is responsible for pooler mode and transaction placement.</summary>
    public async ValueTask ClearSessionStateAsync(MpgsqlSessionCleanup flags, CancellationToken cancellationToken = default)
    {
        const MpgsqlSessionCleanup known = MpgsqlSessionCleanup.Settings | MpgsqlSessionCleanup.ListenSubscriptions
            | MpgsqlSessionCleanup.AdvisoryLocks | MpgsqlSessionCleanup.Cursors | MpgsqlSessionCleanup.TemporaryObjects;
        if ((flags & ~known) != 0) throw new ArgumentOutOfRangeException(nameof(flags));
        cancellationToken.ThrowIfCancellationRequested();
        lock (Gate) CheckAvailable();
        if (flags == MpgsqlSessionCleanup.None) return;
        await using var batch = CreateBatch();
        if (flags.HasFlag(MpgsqlSessionCleanup.Settings)) batch.Commands.Add(batch.CreateCommand("RESET ALL"));
        if (flags.HasFlag(MpgsqlSessionCleanup.ListenSubscriptions)) batch.Commands.Add(batch.CreateCommand("UNLISTEN *"));
        if (flags.HasFlag(MpgsqlSessionCleanup.AdvisoryLocks)) batch.Commands.Add(batch.CreateCommand("SELECT pg_catalog.pg_advisory_unlock_all()"));
        if (flags.HasFlag(MpgsqlSessionCleanup.Cursors)) batch.Commands.Add(batch.CreateCommand("CLOSE ALL"));
        if (flags.HasFlag(MpgsqlSessionCleanup.TemporaryObjects)) batch.Commands.Add(batch.CreateCommand("DISCARD TEMP"));
        await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        lock (Gate)
        {
            if (_dispose is not null) return new(_dispose);
            _disposed = true;
            return new(_dispose = DisposeCoreAsync(_active));
        }
    }

    private async Task DisposeCoreAsync(QueryExecution? active)
    {
        try { if (active is not null) await active.FinishAsync(discard: true).ConfigureAwait(false); }
        finally { await Source.ReturnConnectionAsync(Pooled).ConfigureAwait(false); }
    }
}
