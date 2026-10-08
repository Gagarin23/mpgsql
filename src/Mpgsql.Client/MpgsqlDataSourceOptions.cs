namespace Mpgsql;

/// <summary>Limits for DataSource-owned transports. Options are copied when the source is created.</summary>
public sealed class MpgsqlDataSourceOptions
{
    public int MaxConnections { get; init; } = 10;
    public int MaxInFlightPerConnection { get; init; } = 8;
    /// <summary>Independent requests per shared Sync boundary. One preserves independent boundaries.
    /// Values greater than one opt into a common transaction/error boundary for DataSource requests;
    /// explicitly leased connections and their commands/batches are unaffected.</summary>
    public int SyncGroupSize { get; init; } = 1;
    /// <summary>Maximum scheduling delay from the first admitted request to queuing its group's Sync.
    /// Used only when SyncGroupSize is greater than one. The timer runs independently of readers;
    /// transport backpressure and operating-system scheduling can delay delivery.</summary>
    public TimeSpan SyncGroupTimeout { get; init; } = TimeSpan.FromMilliseconds(1);
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
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(SyncGroupSize);
        if (SyncGroupTimeout < TimeSpan.FromMilliseconds(1) || SyncGroupTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(SyncGroupTimeout));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxBufferedRowBytesPerConnection);
        if (RecoveryTimeout <= TimeSpan.Zero || RecoveryTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(RecoveryTimeout));
        return new()
        {
            MaxConnections = MaxConnections,
            MaxInFlightPerConnection = MaxInFlightPerConnection,
            SyncGroupSize = SyncGroupSize,
            SyncGroupTimeout = SyncGroupTimeout,
            MaxBufferedRowBytesPerConnection = MaxBufferedRowBytesPerConnection,
            RecoveryTimeout = RecoveryTimeout
        };
    }
}
