using System.Globalization;
using Mpgsql.Converters;
using Mpgsql.Types;

namespace Mpgsql.IntegrationTests;

internal static partial class BuiltinConverterChecks
{
    private static void NumericSimd(TestConnection connection)
    {
        long[] longPattern = [long.MinValue, long.MaxValue, -1, 0, 1, 0x0102030405060708];
        var longs = Enumerable.Range(0, 257).Select(i => longPattern[i % longPattern.Length]).ToArray();
        var longPayload = new byte[Int64ArrayConverter.GetByteCount(longs)];
        Int64ArrayConverter.Write(longs, longPayload);
        var longLiteral = "array[" + string.Join(',', longs.Select(value => "'" + value.ToString(CultureInfo.InvariantCulture) + "'::int8")) + "]";
        var longResult = RoundTrip(connection, TypeOid.Int64Array, longLiteral, longPayload)!.Value;
        Check(Int64ArrayConverter.Read(longResult).Span.SequenceEqual(longs), TypeOid.Int64Array, "shared SIMD owned decoding");
        var longStorage = new long[longs.Length];
        Check(Int64ArrayConverter.Read(longResult, longStorage) == longs.Length && longStorage.AsSpan().SequenceEqual(longs),
            TypeOid.Int64Array, "shared SIMD reusable decoding");
        Console.WriteLine("PASS Int64Array shared SIMD blocks/tail against independent PostgreSQL literals");

        NumericArray<short, Int16Codec>(connection, TypeOid.Int16Array,
            new short[] {short.MinValue, short.MaxValue, -1, 0, 1, 0x1234},
            new[] {"'-32768'::int2", "32767::int2", "-1::int2", "0::int2", "1::int2", "4660::int2"});
        NumericArray<int, Int32Codec>(connection, TypeOid.Int32Array,
            new[] {int.MinValue, int.MaxValue, -1, 0, 1, 0x12345678},
            new[] {"'-2147483648'::int4", "2147483647::int4", "-1::int4", "0::int4", "1::int4", "305419896::int4"});
        NumericArray<uint, OidCodec>(connection, TypeOid.OidArray,
            new uint[] {0, uint.MaxValue, 1, 0x80000000, 0x12345678},
            new[] {"0::oid", "'4294967295'::oid", "1::oid", "'2147483648'::oid", "305419896::oid"});
        NumericArray<long, MoneyCodec>(connection, TypeOid.MoneyArray,
            new[] {-12345, 0, 1, 250, 9876543210},
            new[] {"'-123.45'::money", "'0'::money", "'0.01'::money", "'2.50'::money", "'98765432.10'::money"});
        NumericArray<float, Float32Codec>(connection, TypeOid.Float32Array,
            new[] {1.5f, -2.5f, 0f, BitConverter.UInt32BitsToSingle(0x80000000), float.PositiveInfinity, float.NegativeInfinity},
            new[] {"'1.5'::float4", "'-2.5'::float4", "'0'::float4", "'-0'::float4", "'Infinity'::float4", "'-Infinity'::float4"});
        NumericArray<double, Float64Codec>(connection, TypeOid.Float64Array,
            new[] {1.5d, -2.5d, 0d, BitConverter.UInt64BitsToDouble(0x8000000000000000), double.PositiveInfinity, double.NegativeInfinity},
            new[] {"'1.5'::float8", "'-2.5'::float8", "'0'::float8", "'-0'::float8", "'Infinity'::float8", "'-Infinity'::float8"});

        var digits = Enumerable.Range(0, 257).Select(i => (ushort)(1 + i * 7919 % 9999)).ToArray();
        var numeric = new PgNumeric((short)(digits.Length - 1), 0, PgNumericSign.Positive, digits);
        var payload = new byte[NumericConverter.GetByteCount(numeric)];
        NumericConverter.Write(numeric, payload);
        var literal = "'" + string.Concat(digits.Select(digit => digit.ToString("D4", CultureInfo.InvariantCulture))).TrimStart('0') + "'::numeric";
        var result = RoundTrip(connection, TypeOid.Numeric, literal, payload)!.Value;
        Check(NumericConverter.Read(result).Digits.Span.SequenceEqual(digits), TypeOid.Numeric, "SIMD numeric digits");
        PgNumeric[] numerics = [numeric, numeric];
        payload = new byte[NumericArrayConverter.GetByteCount(numerics)];
        NumericArrayConverter.Write(numerics, payload);
        result = RoundTrip(connection, TypeOid.NumericArray, "array[" + literal + "," + literal + "]", payload)!.Value;
        Check(NumericArrayConverter.Read(result).Span[1].Digits.Span.SequenceEqual(digits), TypeOid.NumericArray, "SIMD numeric array digits");
        Console.WriteLine("PASS numeric SIMD scalar/array digits against independent PostgreSQL literals");
    }

    private static void NumericArray<T, TCodec>(TestConnection connection, TypeOid oid,
        T[] pattern, string[] literals)
        where T : struct where TCodec : struct, IBinaryCodec<T>
    {
        var values = Enumerable.Range(0, 257).Select(i => pattern[i % pattern.Length]).ToArray();
        var payload = new byte[BinaryArray<T, TCodec>.Measure(values)];
        BinaryArray<T, TCodec>.Write(values, payload);
        var literal = "array[" + string.Join(',', Enumerable.Range(0, values.Length).Select(i => literals[i % literals.Length])) + "]";
        var result = RoundTrip(connection, oid, literal, payload)!.Value;
        var rewritten = new byte[payload.Length];
        BinaryArray<T, TCodec>.Write(BinaryArray<T, TCodec>.Read(result), rewritten);
        Check(rewritten.AsSpan().SequenceEqual(payload), oid, "SIMD decoder and scalar tail");
        Console.WriteLine("PASS " + oid + " SIMD blocks/tail against independent PostgreSQL literals");
    }
}