namespace Mpgsql;

/// <summary>Explicit server-side cleanup categories. No category deallocates prepared statements.</summary>
[Flags]
public enum MpgsqlSessionCleanup
{
    None = 0,
    Settings = 1,
    ListenSubscriptions = 2,
    AdvisoryLocks = 4,
    Cursors = 8,
    TemporaryObjects = 16
}
