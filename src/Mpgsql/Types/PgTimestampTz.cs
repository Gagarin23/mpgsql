namespace Mpgsql.Types;

/// <summary>PostgreSQL timestamptz: UTC microseconds since 2000-01-01; no zone identity is stored.</summary>
public readonly record struct PgTimestampTz(long MicrosecondsSinceEpoch)
{
    public static PgTimestampTz NegativeInfinity => new(long.MinValue);
    public static PgTimestampTz PositiveInfinity => new(long.MaxValue);
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
        return new((value.Ticks - PgTimestamp.EpochTicks) / 10);
    }
    public static PgTimestampTz FromDateTimeOffset(DateTimeOffset value) => FromDateTime(value.UtcDateTime);
    public DateTime ToDateTime() => DateTime.SpecifyKind(new PgTimestamp(MicrosecondsSinceEpoch).ToDateTime(), DateTimeKind.Utc);
    public DateTimeOffset ToDateTimeOffset() => new(ToDateTime());
}