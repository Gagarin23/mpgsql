namespace Mpgsql.Types;

/// <summary>Sign/special field in PostgreSQL's binary numeric format.</summary>
public enum PgNumericSign : ushort
{
    Positive = 0x0000,
    Negative = 0x4000,
    NaN = 0xc000,
    PositiveInfinity = 0xd000,
    NegativeInfinity = 0xf000
}