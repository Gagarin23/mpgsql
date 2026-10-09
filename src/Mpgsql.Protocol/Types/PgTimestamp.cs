namespace Mpgsql.Types;

/// <summary>PostgreSQL timestamp without time zone: microseconds since 2000-01-01.</summary>
public readonly record struct PgTimestamp(long MicrosecondsSinceEpoch)
{
    public const long MinFiniteMicroseconds = -211_813_488_000_000_000;
    public const long MaxFiniteMicroseconds = 9_223_371_331_199_999_999;
    internal const long EpochTicks = 630_822_816_000_000_000;
    public static PgTimestamp NegativeInfinity => new PgTimestamp(long.MinValue);
    public static PgTimestamp PositiveInfinity => new PgTimestamp(long.MaxValue);
    public bool IsFinite => MicrosecondsSinceEpoch != long.MinValue && MicrosecondsSinceEpoch != long.MaxValue;
    public static PgTimestamp FromDateTime(DateTime value)
    {
        if (value.Kind != DateTimeKind.Unspecified)
        {
            throw new ArgumentException("timestamp requires DateTimeKind.Unspecified.", nameof(value));
        }
        if (value.Ticks % 10 != 0)
        {
            throw new ArgumentException("PostgreSQL timestamps require exact microsecond precision.", nameof(value));
        }
        return new PgTimestamp((value.Ticks - EpochTicks) / 10);
    }
    public DateTime ToDateTime()
    {
        return new DateTime(GetDateTimeTicks(), DateTimeKind.Unspecified);
    }
    internal long GetDateTimeTicks()
    {
        if (!IsFinite)
        {
            throw new OverflowException("DateTime cannot represent timestamp infinity.");
        }
        var ticks = checked(MicrosecondsSinceEpoch * 10 + EpochTicks);
        if ((ulong)ticks > (ulong)DateTime.MaxValue.Ticks)
        {
            throw new OverflowException("The PostgreSQL timestamp is outside DateTime's range.");
        }
        return ticks;
    }
}
