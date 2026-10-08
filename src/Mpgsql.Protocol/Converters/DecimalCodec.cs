using System.Buffers;

namespace Mpgsql.Converters;

internal readonly struct DecimalCodec : IBinaryCodec<decimal>
{
    public static uint Oid => (uint)TypeOid.Numeric;
    public static int FixedSize => 0;
    public static bool MayOverlap => false;
    public static int Measure(decimal value)
    {
        Span<ushort> digits = stackalloc ushort[8];
        return 8 + 2 * NumericCodec.DecimalParts(value, digits, out _, out _, out _);
    }
    public static void CheckOverlap(decimal value, Span<byte> destination) { }
    public static int Write(decimal value, Span<byte> destination)
    {
        Span<ushort> digits = stackalloc ushort[8];
        var count = NumericCodec.DecimalParts(value, digits, out var weight, out var scale, out var sign);
        return NumericCodec.WriteParts(weight, scale, sign, digits[..count], destination);
    }
    public static decimal Read(ReadOnlySpan<byte> payload)
    {
        return NumericCodec.ReadDecimal(payload);
    }
    public static decimal Read(ReadOnlySequence<byte> payload)
    {
        return NumericCodec.ReadDecimal(payload);
    }
}