namespace Mpgsql.Types;

/// <summary>PostgreSQL date: days since 2000-01-01, including BC dates and infinities.</summary>
public readonly record struct PgDate(int DaysSinceEpoch)
{
    public const int MinFiniteDays = -2451545;
    public const int MaxFiniteDays = 2145031948;
    private const int EpochDayNumber = 730119;
    public static PgDate NegativeInfinity => new PgDate(int.MinValue);
    public static PgDate PositiveInfinity => new PgDate(int.MaxValue);
    public bool IsFinite => DaysSinceEpoch != int.MinValue && DaysSinceEpoch != int.MaxValue;
    public static PgDate FromDateOnly(DateOnly value)
    {
        return new PgDate(value.DayNumber - EpochDayNumber);
    }
    public DateOnly ToDateOnly()
    {
        // Adding the epoch can wrap only into a negative Int32. Its unsigned
        // value is still outside DateOnly's range, including both infinities.
        var day = unchecked(DaysSinceEpoch + EpochDayNumber);
        if ((uint)day > (uint)DateOnly.MaxValue.DayNumber)
        {
            throw new OverflowException("The PostgreSQL date is outside DateOnly's range.");
        }
        return DateOnly.FromDayNumber(day);
    }
}
