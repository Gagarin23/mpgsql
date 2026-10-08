using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

internal readonly struct DateClrCodec : IBinaryCodec<DateOnly>
{
    public static uint Oid => (uint)TypeOid.Date;
    public static int FixedSize => 4;
    public static bool MayOverlap => false;
    // Every DateOnly is within PostgreSQL's finite date range.
    public static int Measure(DateOnly value)
    {
        return FixedSize;
    }
    public static void CheckOverlap(DateOnly value, Span<byte> destination) { }
    public static int Write(DateOnly value, Span<byte> destination)
    {
        return DateCodec.Write(PgDate.FromDateOnly(value), destination);
    }
    public static DateOnly Read(ReadOnlySpan<byte> payload)
    {
        return DateCodec
            .Read(payload)
            .ToDateOnly();
    }
    public static DateOnly Read(ReadOnlySequence<byte> payload)
    {
        return DateCodec
            .Read(payload)
            .ToDateOnly();
    }
}