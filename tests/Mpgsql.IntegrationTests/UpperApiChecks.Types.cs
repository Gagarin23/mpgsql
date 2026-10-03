using System.Buffers;
using System.Net;
using Mpgsql.Types;

namespace Mpgsql.IntegrationTests;

internal static partial class UpperApiChecks
{
    private static async Task TypedValuesAsync(MpgsqlDataSource source, CancellationToken token)
    {
        await Value<bool>(source, "boolean", "true::boolean", true, MpgsqlParameter.Boolean, MpgsqlParameter.NullableBooleanArray, token);
        await Value<short>(source, "int2", "-1234::int2", (short)-1234, MpgsqlParameter.Int16, MpgsqlParameter.NullableInt16Array, token);
        await Value<int>(source, "int4", "-123456789::int4", -123456789, MpgsqlParameter.Int32, MpgsqlParameter.NullableInt32Array, token);
        await Value<long>(source, "int8", "'-9223372036854775808'::int8", long.MinValue, MpgsqlParameter.Int64, MpgsqlParameter.NullableInt64Array, token);
        await Value<float>(source, "float4", "1.5::float4", 1.5f, MpgsqlParameter.Float32, MpgsqlParameter.NullableFloat32Array, token);
        await Value<double>(source, "float8", "-2.5::float8", -2.5d, MpgsqlParameter.Float64, MpgsqlParameter.NullableFloat64Array, token);
        await Value<long>(source, "money", "'-123.45'::money", -12345L, MpgsqlParameter.Money, MpgsqlParameter.NullableMoneyArray, token);
        await Value<uint>(source, "oid", "'4275878552'::oid", 0xfedcba98u, MpgsqlParameter.Oid, MpgsqlParameter.NullableOidArray, token);
        await Value<Guid>(source, "uuid", "'00112233-4455-6677-8899-aabbccddeeff'::uuid", Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), MpgsqlParameter.Uuid, MpgsqlParameter.NullableUuidArray, token);
        await Value<PgDate>(source, "date", "'1999-12-31'::date", new PgDate(-1), MpgsqlParameter.Date, MpgsqlParameter.NullableDateArray, token);
        await Value<PgTime>(source, "time", "'00:00:01.000001'::time", new PgTime(1000001), MpgsqlParameter.Time, MpgsqlParameter.NullableTimeArray, token);
        await Value<PgTimeTz>(source, "timetz", "'00:00:01.000001+05'::timetz", new PgTimeTz(new PgTime(1000001), 18000), MpgsqlParameter.TimeTz, MpgsqlParameter.NullableTimeTzArray, token);
        await Value<PgTimestamp>(source, "timestamp", "'1999-12-31 23:59:59.999999'::timestamp", new PgTimestamp(-1), MpgsqlParameter.Timestamp, MpgsqlParameter.NullableTimestampArray, token);
        await Value<PgTimestampTz>(source, "timestamptz", "'2000-01-01 00:00:01.000001+00'::timestamptz", new PgTimestampTz(1000001), MpgsqlParameter.TimestampTz, MpgsqlParameter.NullableTimestampTzArray, token);
        await Value<PgInterval>(source, "interval", "'-2 mons 3 days -00:00:01.000001'::interval", new PgInterval(-2, 3, -1000001), MpgsqlParameter.Interval, MpgsqlParameter.NullableIntervalArray, token);
        await Value<ReadOnlyMemory<byte>>(source, "bytea", "decode('000180ff','hex')", new ReadOnlyMemory<byte>(new byte[] {0, 1, 128, 255}), MpgsqlParameter.Bytea, MpgsqlParameter.NullableByteaArray, token);
        await Value<PgNumeric>(source, "numeric", "-12345.67::numeric", new PgNumeric(1, 2, PgNumericSign.Negative, new ushort[] {1, 2345, 6700}), MpgsqlParameter.Numeric, MpgsqlParameter.NullableNumericArray, token);
        await Value<PgInet>(source, "inet", "'192.0.2.129/24'::inet", PgInet.FromIPAddress(IPAddress.Parse("192.0.2.129"), 24), MpgsqlParameter.Inet, MpgsqlParameter.NullableInetArray, token);
        await Value<PgInet>(source, "cidr", "'192.0.2.0/24'::cidr", PgInet.FromIPAddress(IPAddress.Parse("192.0.2.0"), 24), MpgsqlParameter.Cidr, MpgsqlParameter.NullableCidrArray, token);
        await ReferenceValue(source, "text", "'Я😀'::text", "Я😀", MpgsqlParameter.Text, MpgsqlParameter.TextArray, token);
        await ReferenceValue(source, "json", "'{\"x\":\"Я😀\"}'::json", "{\"x\":\"Я😀\"}", MpgsqlParameter.Json, MpgsqlParameter.JsonArray, token);
        await Value<Memory<byte>>(source, "jsonb", "'{\"x\": \"Я😀\"}'::jsonb", "{\"x\": \"Я😀\"}"u8.ToArray(), MpgsqlParameter.Jsonb, MpgsqlParameter.NullableJsonbArray, token);
        await JsonbMemoryAsync(source, token);
        await ReferenceValue(source, "xml", "'<a>Я😀</a>'::xml", "<a>Я😀</a>", MpgsqlParameter.Xml, MpgsqlParameter.XmlArray, token);
    }

    private static async Task JsonbMemoryAsync(MpgsqlDataSource source, CancellationToken token)
    {
        Memory<byte> json = "{\"x\": \"Я😀\"}"u8.ToArray();
        Memory<byte> jsonNull = "null"u8.ToArray();
        MpgsqlParameter[] parameters = [MpgsqlParameter.JsonbArray(new Memory<byte>[] {json, jsonNull}), MpgsqlParameter.Jsonb(jsonNull)];
        var reader = await source.ExecuteReaderAsync("select $1, ARRAY['{\"x\": \"Я😀\"}'::jsonb, 'null'::jsonb], $2, 'null'::jsonb", parameters, token);
        await using (reader)
        {
            Check(await reader.ReadAsync(), "jsonb owned memory row");
            for (int i = 0; i < 4; i += 2)
                Check(reader.GetRawValue(i)!.Value.ToArray().AsSpan().SequenceEqual(reader.GetRawValue(i + 1)!.Value.ToArray()), "jsonb non-null array and JSON null bytes");
            var array = reader.GetFieldValue<ReadOnlyMemory<Memory<byte>>>(0);
            var nullable = reader.GetFieldValue<ReadOnlyMemory<Memory<byte>?>?>(0)!.Value;
            var scalar = reader.GetFieldValue<Memory<byte>?>(2)!.Value;
            Check(array.Length == 2 && array.Span[0].Span.SequenceEqual(json.Span) && array.Span[1].Span.SequenceEqual(jsonNull.Span), "jsonb non-null array getter");
            Check(nullable.Span[0]!.Value.Span.SequenceEqual(json.Span) && nullable.Span[1]!.Value.Span.SequenceEqual(jsonNull.Span), "jsonb nullable array getter without NULL elements");
            Check(!reader.IsDBNull(2) && scalar.Span.SequenceEqual(jsonNull.Span), "JSON null differs from SQL NULL");
            try { reader.GetFieldValue<string>(2); throw new InvalidDataException("Jsonb accepted a string getter."); }
            catch (InvalidCastException) { }
            Check(!await reader.NextResultAsync(), "jsonb result boundary");
            await reader.DisposeAsync();
            Check(array.Span[0].Span.SequenceEqual(json.Span) && scalar.Span.SequenceEqual(jsonNull.Span), "jsonb memory outlives the result reader");
        }
    }

    private static async Task Value<T>(MpgsqlDataSource source, string sqlType, string literal, T value,
        Func<T?, MpgsqlParameter> scalar, Func<ReadOnlyMemory<T?>?, MpgsqlParameter> array, CancellationToken token) where T : struct
    {
        T?[] items = [value, null, value];
        MpgsqlParameter[] parameters = [scalar(value), array(items), array(ReadOnlyMemory<T?>.Empty), scalar(null), array(null)];
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

    private static async Task ReferenceValue(MpgsqlDataSource source, string sqlType, string literal, string value,
        Func<string?, MpgsqlParameter> scalar, Func<ReadOnlyMemory<string?>?, MpgsqlParameter> array, CancellationToken token)
    {
        string?[] items = [value, null, value];
        MpgsqlParameter[] parameters = [scalar(value), array(items), array(ReadOnlyMemory<string?>.Empty), scalar(null), array(null)];
        await using var reader = await source.ExecuteReaderAsync(Sql(sqlType, literal), parameters, token);
        Check(await reader.ReadAsync(), sqlType + " typed row"); Compare(reader, sqlType);
        Check(reader.GetFieldValue<string>(0) == value, sqlType + " string getter");
        Check(reader.GetFieldValue<ReadOnlyMemory<string?>>(2).Span.SequenceEqual(items), sqlType + " reference array getter");
        Check(reader.GetFieldValue<string>(6) is null, sqlType + " nullable reference getter");
        Check(!await reader.NextResultAsync(), sqlType + " result boundary");
    }

    private static string Sql(string type, string literal)
        => "select $1, " + literal + ", $2, ARRAY[" + literal + ", NULL, " + literal + "], $3, ARRAY[]::" + type + "[], $4::" + type + ", $5::" + type + "[]";

    private static void Compare(MpgsqlResultReader reader, string type)
    {
        for (int i = 0; i < 6; i += 2)
            Check(reader.GetRawValue(i)!.Value.ToArray().AsSpan().SequenceEqual(reader.GetRawValue(i+1)!.Value.ToArray()), type + " independent SQL bytes");
        Check(reader.IsDBNull(6) && reader.IsDBNull(7), type + " outer SQL NULL");
    }

    private static byte[] Encode(MpgsqlParameter parameter)
    {
        byte[] bytes = new byte[parameter.PayloadLength]; parameter.WritePayload(bytes); return bytes;
    }
}
