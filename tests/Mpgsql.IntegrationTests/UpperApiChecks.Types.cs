using System.Buffers;
using System.Net;
using Mpgsql.Types;

namespace Mpgsql.IntegrationTests;

internal static partial class UpperApiChecks
{
    private static async Task TypedValuesAsync(MpgsqlMultiplexingDataSource source, CancellationToken token)
    {
        await Value(source, "boolean", "true::boolean", true, MpgsqlParameterValue.Boolean, MpgsqlParameterValue.NullableBooleanArray, token);
        await Value(source, "int2", "-1234::int2", (short)-1234, MpgsqlParameterValue.Int16, MpgsqlParameterValue.NullableInt16Array, token);
        await Value(source, "int4", "-123456789::int4", -123456789, MpgsqlParameterValue.Int32, MpgsqlParameterValue.NullableInt32Array, token);
        await Value(source, "int8", "'-9223372036854775808'::int8", long.MinValue, MpgsqlParameterValue.Int64, MpgsqlParameterValue.NullableInt64Array, token);
        await Value(source, "float4", "1.5::float4", 1.5f, MpgsqlParameterValue.Float32, MpgsqlParameterValue.NullableFloat32Array, token);
        await Value(source, "float8", "-2.5::float8", -2.5d, MpgsqlParameterValue.Float64, MpgsqlParameterValue.NullableFloat64Array, token);
        await Value(source, "money", "'-123.45'::money", -12345L, MpgsqlParameterValue.Money, MpgsqlParameterValue.NullableMoneyArray, token);
        await Value(source, "oid", "'4275878552'::oid", 0xfedcba98u, MpgsqlParameterValue.Oid, MpgsqlParameterValue.NullableOidArray, token);
        await Value(source, "uuid", "'00112233-4455-6677-8899-aabbccddeeff'::uuid", Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), MpgsqlParameterValue.Uuid, MpgsqlParameterValue.NullableUuidArray, token);
        await Value(source, "date", "'1999-12-31'::date", new PgDate(-1), MpgsqlParameterValue.Date, MpgsqlParameterValue.NullableDateArray, token);
        await Value(source, "time", "'00:00:01.000001'::time", new PgTime(1000001), MpgsqlParameterValue.Time, MpgsqlParameterValue.NullableTimeArray, token);
        await Value(source, "timetz", "'00:00:01.000001+05'::timetz", new PgTimeTz(new PgTime(1000001), 18000), MpgsqlParameterValue.TimeTz, MpgsqlParameterValue.NullableTimeTzArray, token);
        await Value(source, "timestamp", "'1999-12-31 23:59:59.999999'::timestamp", new PgTimestamp(-1), MpgsqlParameterValue.Timestamp, MpgsqlParameterValue.NullableTimestampArray, token);
        await Value(source, "timestamptz", "'2000-01-01 00:00:01.000001+00'::timestamptz", new PgTimestampTz(1000001), MpgsqlParameterValue.TimestampTz, MpgsqlParameterValue.NullableTimestampTzArray, token);
        await Value(source, "interval", "'-2 mons 3 days -00:00:01.000001'::interval", new PgInterval(-2, 3, -1000001), MpgsqlParameterValue.Interval, MpgsqlParameterValue.NullableIntervalArray, token);
        await Value(source, "bytea", "decode('000180ff','hex')", new ReadOnlyMemory<byte>(new byte[] {0, 1, 128, 255}), MpgsqlParameterValue.Bytea, MpgsqlParameterValue.NullableByteaArray, token);
        await Value(source, "numeric", "-12345.67::numeric", new PgNumeric(1, 2, PgNumericSign.Negative, new ushort[] {1, 2345, 6700}), MpgsqlParameterValue.Numeric, MpgsqlParameterValue.NullableNumericArray, token);
        await Value(source, "inet", "'192.0.2.129/24'::inet", PgInet.FromIPAddress(IPAddress.Parse("192.0.2.129"), 24), MpgsqlParameterValue.Inet, MpgsqlParameterValue.NullableInetArray, token);
        await Value(source, "cidr", "'192.0.2.0/24'::cidr", PgInet.FromIPAddress(IPAddress.Parse("192.0.2.0"), 24), MpgsqlParameterValue.Cidr, MpgsqlParameterValue.NullableCidrArray, token);
        await ReferenceValue(source, "text", "'Я😀'::text", "Я😀", MpgsqlParameterValue.Text, MpgsqlParameterValue.TextArray, token);
        await ReferenceValue(source, "varchar", "'Я😀'::varchar", "Я😀", MpgsqlParameterValue.VarChar, MpgsqlParameterValue.VarCharArray, token);
        await ReferenceValue(source, "bpchar", "'Я😀  '::bpchar", "Я😀  ", MpgsqlParameterValue.BpChar, MpgsqlParameterValue.BpCharArray, token);
        await ReferenceValue(source, "name", "'Я😀'::name", "Я😀", MpgsqlParameterValue.Name, MpgsqlParameterValue.NameArray, token);
        await ReferenceValue(source, "json", "'{\"x\":\"Я😀\"}'::json", "{\"x\":\"Я😀\"}", MpgsqlParameterValue.Json, MpgsqlParameterValue.JsonArray, token);
        await Value<Memory<byte>>(source, "jsonb", "'{\"x\": \"Я😀\"}'::jsonb", "{\"x\": \"Я😀\"}"u8.ToArray(), MpgsqlParameterValue.Jsonb, MpgsqlParameterValue.NullableJsonbArray, token);
        await JsonbMemoryAsync(source, token);
        await ReferenceValue(source, "xml", "'<a>Я😀</a>'::xml", "<a>Я😀</a>", MpgsqlParameterValue.Xml, MpgsqlParameterValue.XmlArray, token);
    }

    private static async Task JsonbMemoryAsync(MpgsqlMultiplexingDataSource source, CancellationToken token)
    {
        Memory<byte> json = "{\"x\": \"Я😀\"}"u8.ToArray();
        Memory<byte> jsonNull = "null"u8.ToArray();
        MpgsqlParameterValue[] parameters = [MpgsqlParameterValue.JsonbArray(new[] {json, jsonNull}), MpgsqlParameterValue.Jsonb(jsonNull)];
        var reader = await source.ExecuteReaderAsync("select $1, ARRAY['{\"x\": \"Я😀\"}'::jsonb, 'null'::jsonb], $2, 'null'::jsonb", parameters, token);
        await using (reader)
        {
            Check(await reader.ReadAsync(), "jsonb owned memory row");
            for (var i = 0; i < 4; i += 2)
            {
                Check(reader.GetRawValue(i)!.Value.ToArray().AsSpan().SequenceEqual(reader.GetRawValue(i + 1)!.Value.ToArray()), "jsonb non-null array and JSON null bytes");
            }
            var array = reader.GetFieldValue<ReadOnlyMemory<Memory<byte>>>(0);
            var nullable = reader.GetFieldValue<ReadOnlyMemory<Memory<byte>?>?>(0)!.Value;
            var scalar = reader.GetFieldValue<Memory<byte>?>(2)!.Value;
            Check(array.Length == 2 && array.Span[0].Span.SequenceEqual(json.Span) && array.Span[1].Span.SequenceEqual(jsonNull.Span), "jsonb non-null array getter");
            Check(nullable.Span[0]!.Value.Span.SequenceEqual(json.Span) && nullable.Span[1]!.Value.Span.SequenceEqual(jsonNull.Span), "jsonb nullable array getter without NULL elements");
            Check(!reader.IsDBNull(2) && scalar.Span.SequenceEqual(jsonNull.Span), "JSON null differs from SQL NULL");
            try
            {
                reader.GetFieldValue<string>(2);
                throw new InvalidDataException("Jsonb accepted a string getter.");
            }
            catch (InvalidCastException) { }
            Check(!await reader.NextResultAsync(), "jsonb result boundary");
            await reader.DisposeAsync();
            Check(array.Span[0].Span.SequenceEqual(json.Span) && scalar.Span.SequenceEqual(jsonNull.Span), "jsonb memory outlives the result reader");
        }
    }

    private static async Task Value<T>(MpgsqlMultiplexingDataSource source, string sqlType,
        string literal, T value,
        Func<T?, MpgsqlParameterValue> scalar, Func<ReadOnlyMemory<T?>?, MpgsqlParameterValue> array,
        CancellationToken token) where T : struct
    {
        T?[] items = [value, null, value];
        MpgsqlParameterValue[] parameters = [scalar(value), array(items), array(ReadOnlyMemory<T?>.Empty), scalar(null), array(null)];
        await using var reader = await source.ExecuteReaderAsync(Sql(sqlType, literal), parameters, token);
        Check(await reader.ReadAsync(), sqlType + " typed row");
        Compare(reader, sqlType);
        var decoded = scalar(reader.GetFieldValue<T>(0));
        var decodedArray = array(reader.GetFieldValue<ReadOnlyMemory<T?>>(2));
        Check(Encode(decoded).AsSpan().SequenceEqual(reader.GetRawValue(0)!.Value.ToArray()), sqlType + " scalar getter");
        Check(Encode(decodedArray).AsSpan().SequenceEqual(reader.GetRawValue(2)!.Value.ToArray()), sqlType + " array getter");
        Check(reader.GetFieldValue<T?>(6) is null && reader.GetFieldValue<ReadOnlyMemory<T?>?>(7) is null, sqlType + " nullable getters");
        Check(!await reader.NextResultAsync(), sqlType + " result boundary");
    }

    private static async Task ReferenceValue(MpgsqlMultiplexingDataSource source, string sqlType,
        string literal, string value,
        Func<string?, MpgsqlParameterValue> scalar, Func<ReadOnlyMemory<string?>?, MpgsqlParameterValue> array,
        CancellationToken token)
    {
        string?[] items = [value, null, value];
        MpgsqlParameterValue[] parameters = [scalar(value), array(items), array(ReadOnlyMemory<string?>.Empty), scalar(null), array(null)];
        await using var reader = await source.ExecuteReaderAsync(Sql(sqlType, literal), parameters, token);
        Check(await reader.ReadAsync(), sqlType + " typed row");
        Compare(reader, sqlType);
        Check(reader.GetFieldValue<string>(0) == value, sqlType + " string getter");
        Check(reader.GetFieldValue<ReadOnlyMemory<string?>>(2).Span.SequenceEqual(items), sqlType + " reference array getter");
        Check(reader.GetFieldValue<string>(6) is null, sqlType + " nullable reference getter");
        Check(!await reader.NextResultAsync(), sqlType + " result boundary");
    }

    private static string Sql(string type, string literal)
    {
        return "select $1, " + literal + ", $2, ARRAY[" + literal + ", NULL, " + literal + "], $3, ARRAY[]::" + type + "[], $4::" + type + ", $5::" + type + "[]";
    }

    private static void Compare(MpgsqlResultReader reader, string type)
    {
        for (var i = 0; i < 6; i += 2)
        {
            Check(reader.GetRawValue(i)!.Value.ToArray().AsSpan().SequenceEqual(reader.GetRawValue(i + 1)!.Value.ToArray()), type + " independent SQL bytes");
        }
        Check(reader.IsDBNull(6) && reader.IsDBNull(7), type + " outer SQL NULL");
    }

    private static byte[] Encode(MpgsqlParameterValue parameter)
    {
        var bytes = new byte[parameter.PayloadLength];
        parameter.WritePayload(bytes);
        return bytes;
    }
}