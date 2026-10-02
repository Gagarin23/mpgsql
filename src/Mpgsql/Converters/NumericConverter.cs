using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

/// <summary>Lossless PostgreSQL numeric, plus exact Decimal conversion without text formatting.</summary>
public static partial class NumericConverter
{
    public const uint TypeOid = (uint)Mpgsql.TypeOid.Numeric;
    public static int GetByteCount(PgNumeric value) => NumericCodec.Measure(value);
    public static int GetByteCount(PgNumeric? value) => value.HasValue ? GetByteCount(value.Value) : 0;
    public static int Write(PgNumeric value, Span<byte> destination) => BinaryScalar<PgNumeric, NumericCodec>.Write(value, destination);
    public static int Write(PgNumeric? value, Span<byte> destination) => value.HasValue ? Write(value.Value, destination) : 0;
    public static void Write(PgNumeric value, IBufferWriter<byte> destination) => BinaryScalar<PgNumeric, NumericCodec>.Write(value, destination);
    public static void Write(PgNumeric? value, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (value.HasValue)
        {
            Write(value.Value, destination);
        }
    }
    public static PgNumeric Read(ReadOnlySpan<byte> payload) => NumericCodec.Read(payload);
    public static PgNumeric Read(ReadOnlySequence<byte> payload) => NumericCodec.Read(payload);
    /// <summary>Uses caller-owned digit storage; returned numeric borrows that storage.</summary>
    /// <remarks>Capacity/overlap are checked before writing; malformed digits may leave a partial result.</remarks>
    public static PgNumeric Read(ReadOnlySpan<byte> payload, Memory<ushort> digits) => NumericCodec.Read(payload, digits);
    public static PgNumeric Read(ReadOnlySequence<byte> payload, Memory<ushort> digits) => NumericCodec.Read(payload, digits);
    public static PgNumeric? ReadNullable(ReadOnlyMemory<byte>? payload) => payload is { } value ? Read(value.Span) : null;
    public static PgNumeric? ReadNullable(ReadOnlySequence<byte>? payload) => payload is { } value ? Read(value) : null;

    public static int GetByteCount(decimal value) => DecimalCodec.Measure(value);
    public static int GetByteCount(decimal? value) => value.HasValue ? GetByteCount(value.Value) : 0;
    public static int Write(decimal value, Span<byte> destination) => BinaryScalar<decimal, DecimalCodec>.Write(value, destination);
    public static int Write(decimal? value, Span<byte> destination) => value.HasValue ? Write(value.Value, destination) : 0;
    public static void Write(decimal value, IBufferWriter<byte> destination) => BinaryScalar<decimal, DecimalCodec>.Write(value, destination);
    public static void Write(decimal? value, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (value.HasValue)
        {
            Write(value.Value, destination);
        }
    }
    /// <summary>Reads exactly, throwing OverflowException instead of rounding an unrepresentable value.</summary>
    public static decimal ReadDecimal(ReadOnlySpan<byte> payload) => DecimalCodec.Read(payload);
    public static decimal ReadDecimal(ReadOnlySequence<byte> payload) => DecimalCodec.Read(payload);
    public static decimal? ReadNullableDecimal(ReadOnlyMemory<byte>? payload) => payload is { } value ? ReadDecimal(value.Span) : null;
    public static decimal? ReadNullableDecimal(ReadOnlySequence<byte>? payload) => payload is { } value ? ReadDecimal(value) : null;
}