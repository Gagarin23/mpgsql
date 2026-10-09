using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

/// <summary>Lossless PostgreSQL numeric, plus exact Decimal conversion without text formatting.</summary>
public static class NumericConverter
{
    public const uint TypeOid = (uint)Mpgsql.TypeOid.Numeric;
    public static int GetByteCount(PgNumeric value)
    {
        return NumericCodec.Measure(value);
    }
    public static int GetByteCount(PgNumeric? value)
    {
        return value.HasValue ? GetByteCount(value.Value) : 0;
    }
    public static int Write(PgNumeric value, Span<byte> destination)
    {
        return BinaryScalar<PgNumeric, NumericCodec>.Write(value, destination);
    }
    public static int Write(PgNumeric? value, Span<byte> destination)
    {
        return value.HasValue ? Write(value.Value, destination) : 0;
    }
    public static void Write(PgNumeric value, IBufferWriter<byte> destination)
    {
        BinaryScalar<PgNumeric, NumericCodec>.Write(value, destination);
    }
    public static void Write(PgNumeric? value, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (value.HasValue)
        {
            Write(value.Value, destination);
        }
    }
    public static PgNumeric Read(ReadOnlySpan<byte> payload)
    {
        return NumericCodec.Read(payload);
    }
    public static PgNumeric Read(ReadOnlySequence<byte> payload)
    {
        return NumericCodec.Read(payload);
    }
    /// <summary>Uses caller-owned digit storage; returned numeric borrows that storage.</summary>
    /// <remarks>Capacity/overlap are checked before writing; malformed digits may leave a partial result.</remarks>
    public static PgNumeric Read(ReadOnlySpan<byte> payload, Memory<ushort> digits)
    {
        return NumericCodec.Read(payload, digits);
    }
    public static PgNumeric Read(ReadOnlySequence<byte> payload, Memory<ushort> digits)
    {
        return NumericCodec.Read(payload, digits);
    }
    public static PgNumeric? ReadNullable(ReadOnlyMemory<byte>? payload)
    {
        return payload is { } value ? Read(value.Span) : null;
    }
    public static PgNumeric? ReadNullable(ReadOnlySequence<byte>? payload)
    {
        return payload is { } value ? Read(value) : null;
    }

    public static int GetByteCount(decimal value)
    {
        return DecimalCodec.Measure(value);
    }
    public static int GetByteCount(decimal? value)
    {
        return value.HasValue ? GetByteCount(value.Value) : 0;
    }
    public static int Write(decimal value, Span<byte> destination)
    {
        Span<ushort> digits = stackalloc ushort[8];
        var count = NumericCodec.DecimalParts(value, digits, out var weight, out var scale, out var sign);
        var size = 8 + count * 2;
        BinaryPayload.RequireCapacity(size, destination.Length);
        return NumericCodec.WriteParts(weight, scale, sign, digits[..count], destination[..size]);
    }
    public static int Write(decimal? value, Span<byte> destination)
    {
        return value.HasValue ? Write(value.Value, destination) : 0;
    }
    public static void Write(decimal value, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        Span<ushort> digits = stackalloc ushort[8];
        var count = NumericCodec.DecimalParts(value, digits, out var weight, out var scale, out var sign);
        var size = 8 + count * 2;
        var bytes = destination.GetSpan(size)[..size];
        NumericCodec.WriteParts(weight, scale, sign, digits[..count], bytes);
        destination.Advance(size);
    }
    public static void Write(decimal? value, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (value.HasValue)
        {
            Write(value.Value, destination);
        }
    }
    /// <summary>Reads exactly, throwing OverflowException instead of rounding an unrepresentable value.</summary>
    public static decimal ReadDecimal(ReadOnlySpan<byte> payload)
    {
        return DecimalCodec.Read(payload);
    }
    public static decimal ReadDecimal(ReadOnlySequence<byte> payload)
    {
        return DecimalCodec.Read(payload);
    }
    public static decimal? ReadNullableDecimal(ReadOnlyMemory<byte>? payload)
    {
        return payload is { } value ? ReadDecimal(value.Span) : null;
    }
    public static decimal? ReadNullableDecimal(ReadOnlySequence<byte>? payload)
    {
        return payload is { } value ? ReadDecimal(value) : null;
    }
}
