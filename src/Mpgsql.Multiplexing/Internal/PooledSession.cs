namespace Mpgsql.Multiplexing.Internal;

internal sealed class PooledSession(MpgsqlMessageSession session, MpgsqlMultiplexingOptions? options = null)
{
    private readonly Lock _gate = new Lock();

    // Protected by the data source gate.
    internal int Active;
    internal bool Retired;
    private Task? _dispose;
    internal MpgsqlMessageSession Session { get; } = session;
    internal PipelineSyncScheduler? SyncScheduler { get; } = options is {SyncGroupSize: > 1}
        ? new PipelineSyncScheduler(session, options.SyncGroupSize, options.SyncGroupTimeout)
        : null;
    internal Task DisposeAsync()
    {
        lock (_gate)
        {
            if (_dispose is not null)
            {
                return _dispose;
            }
            SyncScheduler?.Dispose();
            return _dispose = Session
                .DisposeAsync()
                .AsTask();
        }
    }
}