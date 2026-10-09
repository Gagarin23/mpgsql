namespace Mpgsql.Types;

/// <summary>PostgreSQL timestamptz: UTC microseconds since 2000-01-01; no zone identity is stored.</summary>
public readonly record struct PgTimestampTz(long MicrosecondsSinceEpoch)
{
    public static PgTimestampTz NegativeInfinity => new PgTimestampTz(long.MinValue);
    public static PgTimestampTz PositiveInfinity => new PgTimestampTz(long.MaxValue);
    public bool IsFinite => MicrosecondsSinceEpoch != long.MinValue && MicrosecondsSinceEpoch != long.MaxValue;
    public static PgTimestampTz FromDateTime(DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("timestamptz requires DateTimeKind.Utc.", nameof(value));
        }
        if (value.Ticks % 10 != 0)
        {
            throw new ArgumentException("PostgreSQL timestamps require exact microsecond precision.", nameof(value));
        }
        return new PgTimestampTz((value.Ticks - PgTimestamp.EpochTicks) / 10);
    }
    public static PgTimestampTz FromDateTimeOffset(DateTimeOffset value)
    {
        return FromDateTime(value.UtcDateTime);
    }
    public DateTime ToDateTime()
    {
        return new DateTime(new PgTimestamp(MicrosecondsSinceEpoch).GetDateTimeTicks(), DateTimeKind.Utc);
    }
    public DateTimeOffset ToDateTimeOffset()
    {
        return new DateTimeOffset(new PgTimestamp(MicrosecondsSinceEpoch).GetDateTimeTicks(), TimeSpan.Zero);
    }
}
