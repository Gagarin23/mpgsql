using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

internal readonly struct IntervalClrCodec : IBinaryCodec<TimeSpan>
{
    public static uint Oid => (uint)TypeOid.Interval;
    public static int FixedSize => 16;
    public static bool MayOverlap => false;
    public static int Measure(TimeSpan value)
    {
        return IntervalCodec.Measure(PgInterval.FromTimeSpan(value));
    }
    public static void CheckOverlap(TimeSpan value, Span<byte> destination) { }
    public static int Write(TimeSpan value, Span<byte> destination)
    {
        return IntervalCodec.Write(PgInterval.FromTimeSpan(value), destination);
    }
    public static TimeSpan Read(ReadOnlySpan<byte> payload)
    {
        return IntervalCodec.Read(payload).ToTimeSpan();
    }
    public static TimeSpan Read(ReadOnlySequence<byte> payload)
    {
        return IntervalCodec.Read(payload).ToTimeSpan();
    }
}