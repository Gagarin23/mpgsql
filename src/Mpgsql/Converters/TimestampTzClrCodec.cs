using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

internal readonly struct TimestampTzClrCodec : IBinaryCodec<DateTimeOffset>
{
    public static uint Oid => (uint)TypeOid.TimestampTz;
    public static int FixedSize => 8;
    public static bool MayOverlap => false;
    public static int Measure(DateTimeOffset value) => TimestampTzCodec.Measure(PgTimestampTz.FromDateTimeOffset(value));
    public static void CheckOverlap(DateTimeOffset value, Span<byte> destination) { }
    public static int Write(DateTimeOffset value, Span<byte> destination) => TimestampTzCodec.Write(PgTimestampTz.FromDateTimeOffset(value), destination);
    public static DateTimeOffset Read(ReadOnlySpan<byte> payload) => TimestampTzCodec.Read(payload).ToDateTimeOffset();
    public static DateTimeOffset Read(ReadOnlySequence<byte> payload) => TimestampTzCodec.Read(payload).ToDateTimeOffset();
}