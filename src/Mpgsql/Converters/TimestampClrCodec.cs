using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

internal readonly struct TimestampClrCodec : IBinaryCodec<DateTime>
{
    public static uint Oid => (uint)TypeOid.Timestamp;
    public static int FixedSize => 8;
    public static bool MayOverlap => false;
    public static int Measure(DateTime value)
    {
        return TimestampCodec.Measure(PgTimestamp.FromDateTime(value));
    }
    public static void CheckOverlap(DateTime value, Span<byte> destination) { }
    public static int Write(DateTime value, Span<byte> destination)
    {
        return TimestampCodec.Write(PgTimestamp.FromDateTime(value), destination);
    }
    public static DateTime Read(ReadOnlySpan<byte> payload)
    {
        return TimestampCodec.Read(payload).ToDateTime();
    }
    public static DateTime Read(ReadOnlySequence<byte> payload)
    {
        return TimestampCodec.Read(payload).ToDateTime();
    }
}