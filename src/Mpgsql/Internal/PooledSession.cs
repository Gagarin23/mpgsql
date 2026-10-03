namespace Mpgsql.Internal;

internal sealed class PooledSession(MpgsqlMessageSession session)
{
    internal MpgsqlMessageSession Session { get; } = session;
    internal readonly SemaphoreSlim Publication = new(1, 1);
    // Protected by the data source gate.
    internal int Active;
    internal bool Leased;
    internal bool Retired;
    private readonly Lock _gate = new();
    private Task? _dispose;
    internal Task DisposeAsync() { lock (_gate) return _dispose ??= Session.DisposeAsync().AsTask(); }
}
