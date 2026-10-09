using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Mpgsql.Types;

/// <summary>PostgreSQL interval retains independent months, days, and microseconds.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct PgInterval(int Months, int Days, long Microseconds)
{
    private const long MaximumFastComponentTicks = long.MaxValue / 2;
    private const int MaximumFastDays = (int)(MaximumFastComponentTicks / TimeSpan.TicksPerDay);
    private const long MaximumFastMicroseconds = MaximumFastComponentTicks / 10;
    public static PgInterval NegativeInfinity => new PgInterval(int.MinValue, int.MinValue, long.MinValue);
    public static PgInterval PositiveInfinity => new PgInterval(int.MaxValue, int.MaxValue, long.MaxValue);
    public bool IsFinite => this != NegativeInfinity && this != PositiveInfinity;
    public static PgInterval FromTimeSpan(TimeSpan value)
    {
        if (value.Ticks % 10 != 0)
        {
            throw new ArgumentException("PostgreSQL intervals require exact microsecond precision.", nameof(value));
        }
        var days = checked((int)(value.Ticks / TimeSpan.TicksPerDay));
        return new PgInterval(0, days, value.Ticks % TimeSpan.TicksPerDay / 10);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TimeSpan ToTimeSpan()
    {
        // Both infinity sentinels have nonzero Months, so this also rejects them
        // without comparing the complete interval twice.
        if (Months != 0)
        {
            throw new InvalidOperationException("An infinite interval or an interval with months has no fixed TimeSpan duration.");
        }
        if (unchecked((uint)(Days + MaximumFastDays)) <= 2U * (uint)MaximumFastDays &&
            unchecked((ulong)(Microseconds + MaximumFastMicroseconds)) <= 2UL * (ulong)MaximumFastMicroseconds)
        {
            // Each signed contribution is at most Int64.MaxValue / 2 in magnitude.
            // Their products and sum therefore fit without intermediate overflow.
            return new TimeSpan(unchecked(Days * TimeSpan.TicksPerDay + Microseconds * 10));
        }
        return ToTimeSpanWide();
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private TimeSpan ToTimeSpanWide()
    {
        // Large opposite components can cancel to a representable duration even
        // when an individual Int64 product would overflow.
        var ticks = (Int128)Days * TimeSpan.TicksPerDay + (Int128)Microseconds * 10;
        return new TimeSpan(checked((long)ticks));
    }
}
