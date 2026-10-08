namespace Mpgsql;

/// <summary>Exclusive pool limits. No multiplexing or automatic session reset.</summary>
public sealed class MpgsqlDataSourceOptions
{
    public int MaxConnections { get; init; } = 10;
    public long MaxBufferedRowBytesPerConnection { get; init; } = 8 * 1024 * 1024;
    public TimeSpan RecoveryTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public MpgsqlTypeMapper? TypeMapper { get; init; }
    internal MpgsqlDataSourceOptions CopyValidated()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxConnections);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxBufferedRowBytesPerConnection);
        if (RecoveryTimeout <= TimeSpan.Zero || RecoveryTimeout.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(RecoveryTimeout));
        }
        return new MpgsqlDataSourceOptions
        {
            MaxConnections = MaxConnections,
            MaxBufferedRowBytesPerConnection = MaxBufferedRowBytesPerConnection,
            RecoveryTimeout = RecoveryTimeout,
            TypeMapper = TypeMapper?.Snapshot()
        };
    }
}