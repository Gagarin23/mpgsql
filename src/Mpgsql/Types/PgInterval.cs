namespace Mpgsql.Types;

/// <summary>PostgreSQL interval retains independent months, days, and microseconds.</summary>
public readonly record struct PgInterval(int Months, int Days, long Microseconds)
{
    public static PgInterval NegativeInfinity => new(int.MinValue, int.MinValue, long.MinValue);
    public static PgInterval PositiveInfinity => new(int.MaxValue, int.MaxValue, long.MaxValue);
    public bool IsFinite => this != NegativeInfinity && this != PositiveInfinity;
    public static PgInterval FromTimeSpan(TimeSpan value)
    {
        if (value.Ticks % 10 != 0)
        {
            throw new ArgumentException("PostgreSQL intervals require exact microsecond precision.", nameof(value));
        }
        int days = checked((int)(value.Ticks / TimeSpan.TicksPerDay));
        return new(0, days, value.Ticks % TimeSpan.TicksPerDay / 10);
    }
    public TimeSpan ToTimeSpan()
    {
        if (!IsFinite || Months != 0)
        {
            throw new InvalidOperationException("An infinite interval or an interval with months has no fixed TimeSpan duration.");
        }
        Int128 ticks = (Int128)Days * TimeSpan.TicksPerDay + (Int128)Microseconds * 10;
        return new(checked((long)ticks));
    }
}