namespace Mpgsql.Types;

/// <summary>PostgreSQL time in microseconds since midnight; permits 24:00:00.</summary>
public readonly record struct PgTime(long Microseconds)
{
    public const long MicrosecondsPerDay = 86_400_000_000;
    public static PgTime FromTimeOnly(TimeOnly value) => FromTimeSpan(value.ToTimeSpan());
    public static PgTime FromTimeSpan(TimeSpan value)
    {
        if (value.Ticks < 0 || value.Ticks > TimeSpan.TicksPerDay || value.Ticks % 10 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Time requires exact microseconds in [00:00, 24:00].");
        }
        return new(value.Ticks / 10);
    }
    public TimeSpan ToTimeSpan()
    {
        if ((ulong)Microseconds > MicrosecondsPerDay)
        {
            throw new OverflowException("Invalid PostgreSQL time.");
        }
        return new(Microseconds * 10);
    }
    public TimeOnly ToTimeOnly()
    {
        if (Microseconds == MicrosecondsPerDay)
        {
            throw new OverflowException("TimeOnly cannot represent 24:00:00.");
        }
        return TimeOnly.FromTimeSpan(ToTimeSpan());
    }
}