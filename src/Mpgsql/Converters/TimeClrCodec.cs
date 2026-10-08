using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

internal readonly struct TimeClrCodec : IBinaryCodec<TimeOnly>
{
    public static uint Oid => (uint)TypeOid.Time;
    public static int FixedSize => 8;
    public static bool MayOverlap => false;
    public static int Measure(TimeOnly value)
    {
        return TimeCodec.Measure(PgTime.FromTimeOnly(value));
    }
    public static void CheckOverlap(TimeOnly value, Span<byte> destination) { }
    public static int Write(TimeOnly value, Span<byte> destination)
    {
        return TimeCodec.Write(PgTime.FromTimeOnly(value), destination);
    }
    public static TimeOnly Read(ReadOnlySpan<byte> payload)
    {
        return TimeCodec
            .Read(payload)
            .ToTimeOnly();
    }
    public static TimeOnly Read(ReadOnlySequence<byte> payload)
    {
        return TimeCodec
            .Read(payload)
            .ToTimeOnly();
    }
}