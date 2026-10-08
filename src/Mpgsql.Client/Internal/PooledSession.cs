namespace Mpgsql.Internal;

internal sealed class PooledSession(MpgsqlMessageSession session, MpgsqlDataSourceOptions? options = null)
{
    internal MpgsqlMessageSession Session { get; } = session;
    internal PipelineSyncScheduler? SyncScheduler { get; } = options is { SyncGroupSize: > 1 }
        ? new(session, options.SyncGroupSize, options.SyncGroupTimeout) : null;
    // Protected by the data source gate.
    internal int Active;
    internal bool Leased;
    internal bool Retired;
    private readonly Lock _gate = new();
    private Task? _dispose;
    internal Task DisposeAsync()
    {
        lock (_gate)
        {
            if (_dispose is not null) return _dispose;
            SyncScheduler?.Dispose();
            return _dispose = Session.DisposeAsync().AsTask();
        }
    }
}
