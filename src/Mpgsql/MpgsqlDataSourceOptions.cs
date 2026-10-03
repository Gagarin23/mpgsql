namespace Mpgsql;

/// <summary>Limits for DataSource-owned transports. Options are copied when the source is created.</summary>
public sealed class MpgsqlDataSourceOptions
{
    public int MaxConnections { get; init; } = 10;
    public int MaxInFlightPerConnection { get; init; } = 8;
    /// <summary>Retained row payload bytes per transport, excluding one incoming frame and pool rounding.
    /// One oversized row can be retained alone to ensure progress.</summary>
    public long MaxBufferedRowBytesPerConnection { get; init; } = 8 * 1024 * 1024;
    /// <summary>Recovery deadline for exclusively leased connections. Multiplexed logical cancellation
    /// never closes a healthy shared transport just because its SQL is still running.</summary>
    public TimeSpan RecoveryTimeout { get; init; } = TimeSpan.FromSeconds(5);

    internal MpgsqlDataSourceOptions CopyValidated()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxConnections);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxInFlightPerConnection);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxBufferedRowBytesPerConnection);
        if (RecoveryTimeout <= TimeSpan.Zero || RecoveryTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(RecoveryTimeout));
        return new()
        {
            MaxConnections = MaxConnections,
            MaxInFlightPerConnection = MaxInFlightPerConnection,
            MaxBufferedRowBytesPerConnection = MaxBufferedRowBytesPerConnection,
            RecoveryTimeout = RecoveryTimeout
        };
    }
}
