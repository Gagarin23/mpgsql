using Mpgsql.Converters;

namespace Mpgsql.Types;

/// <summary>Lossless PostgreSQL numeric: base-10000 digits, signed weight, display scale and sign.</summary>
/// <remarks>
/// Digit i contributes Digits[i] * 10000^(Weight-i). Digits are borrowed on construction;
/// keep their backing storage stable during conversion. Converter reads return owned digits.
/// </remarks>
public readonly record struct PgNumeric
(
    short Weight,
    ushort Scale,
    PgNumericSign Sign,
    ReadOnlyMemory<ushort> Digits
)
{
    public bool IsFinite => Sign is PgNumericSign.Positive or PgNumericSign.Negative;
    public static PgNumeric NaN => new(0, 0, PgNumericSign.NaN, default);
    public static PgNumeric PositiveInfinity => new(0, 0, PgNumericSign.PositiveInfinity, default);
    public static PgNumeric NegativeInfinity => new(0, 0, PgNumericSign.NegativeInfinity, default);
    public static PgNumeric FromDecimal(decimal value) => NumericCodec.FromDecimal(value);
    public decimal ToDecimal() => NumericCodec.ToDecimal(Weight, Scale, Sign, Digits.Span);
}