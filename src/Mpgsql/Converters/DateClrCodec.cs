using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

internal readonly struct DateClrCodec : IBinaryCodec<DateOnly>
{
    public static uint Oid => (uint)TypeOid.Date;
    public static int FixedSize => 4;
    public static bool NeedsValidation => true;
    public static bool MayOverlap => false;
    public static int Measure(DateOnly value) => DateCodec.Measure(PgDate.FromDateOnly(value));
    public static void CheckOverlap(DateOnly value, Span<byte> destination) { }
    public static void Write(DateOnly value, Span<byte> destination) => DateCodec.Write(PgDate.FromDateOnly(value), destination);
    public static DateOnly Read(ReadOnlySpan<byte> payload) => DateCodec.Read(payload).ToDateOnly();
    public static DateOnly Read(ReadOnlySequence<byte> payload) => DateCodec.Read(payload).ToDateOnly();
}