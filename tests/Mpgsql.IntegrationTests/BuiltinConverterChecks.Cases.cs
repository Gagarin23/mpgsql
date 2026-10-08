using System.Buffers;
using System.Net;
using Mpgsql.Converters;
using Mpgsql.Types;

namespace Mpgsql.IntegrationTests;

internal static partial class BuiltinConverterChecks
{
    internal static void Run(TestConnection connection)
    {
        connection.Query("set lc_monetary = 'C'");
        NumericSimd(connection);
        Value<bool>(connection, TypeOid.Boolean, "boolean", "true::boolean", true,
            BooleanConverter.GetByteCount, BooleanConverter.Write, BooleanConverter.Read,
            NullableBooleanArrayConverter.GetByteCount, NullableBooleanArrayConverter.Write, NullableBooleanArrayConverter.Read);
        Value<short>(connection, TypeOid.Int16, "int2", "-1234::int2", (short)-1234,
            Int16Converter.GetByteCount, Int16Converter.Write, Int16Converter.Read,
            NullableInt16ArrayConverter.GetByteCount, NullableInt16ArrayConverter.Write, NullableInt16ArrayConverter.Read);
        Value<int>(connection, TypeOid.Int32, "int4", "-123456789::int4", -123456789,
            Int32Converter.GetByteCount, Int32Converter.Write, Int32Converter.Read,
            NullableInt32ArrayConverter.GetByteCount, NullableInt32ArrayConverter.Write, NullableInt32ArrayConverter.Read);
        Value<long>(connection, TypeOid.Int64, "int8", "'-9223372036854775808'::int8", long.MinValue,
            Int64Converter.GetByteCount, Int64Converter.Write, Int64Converter.Read,
            NullableInt64ArrayConverter.GetByteCount, NullableInt64ArrayConverter.Write, NullableInt64ArrayConverter.Read);
        Value<float>(connection, TypeOid.Float32, "float4", "1.5::float4", 1.5f,
            Float32Converter.GetByteCount, Float32Converter.Write, Float32Converter.Read,
            NullableFloat32ArrayConverter.GetByteCount, NullableFloat32ArrayConverter.Write, NullableFloat32ArrayConverter.Read);
        Value<double>(connection, TypeOid.Float64, "float8", "-2.5::float8", -2.5d,
            Float64Converter.GetByteCount, Float64Converter.Write, Float64Converter.Read,
            NullableFloat64ArrayConverter.GetByteCount, NullableFloat64ArrayConverter.Write, NullableFloat64ArrayConverter.Read);
        Value<long>(connection, TypeOid.Money, "money", "'-123.45'::money", -12345L,
            MoneyConverter.GetByteCount, MoneyConverter.Write, MoneyConverter.Read,
            NullableMoneyArrayConverter.GetByteCount, NullableMoneyArrayConverter.Write, NullableMoneyArrayConverter.Read);
        Value<uint>(connection, TypeOid.Oid, "oid", "'4275878552'::oid", 0xfedcba98u,
            OidConverter.GetByteCount, OidConverter.Write, OidConverter.Read,
            NullableOidArrayConverter.GetByteCount, NullableOidArrayConverter.Write, NullableOidArrayConverter.Read);
        Value<Guid>(connection, TypeOid.Uuid, "uuid", "'00112233-4455-6677-8899-aabbccddeeff'::uuid", Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
            UuidConverter.GetByteCount, UuidConverter.Write, UuidConverter.Read,
            NullableUuidArrayConverter.GetByteCount, NullableUuidArrayConverter.Write, NullableUuidArrayConverter.Read);
        Value<PgDate>(connection, TypeOid.Date, "date", "'1999-12-31'::date", new PgDate(-1),
            DateConverter.GetByteCount, DateConverter.Write, DateConverter.Read,
            NullableDateArrayConverter.GetByteCount, NullableDateArrayConverter.Write, NullableDateArrayConverter.Read);
        Value<PgTime>(connection, TypeOid.Time, "time", "'00:00:01.000001'::time", new PgTime(1000001),
            TimeConverter.GetByteCount, TimeConverter.Write, TimeConverter.Read,
            NullableTimeArrayConverter.GetByteCount, NullableTimeArrayConverter.Write, NullableTimeArrayConverter.Read);
        Value<PgTimeTz>(connection, TypeOid.TimeTz, "timetz", "'00:00:01.000001+05'::timetz", new PgTimeTz(new PgTime(1000001), 18000),
            TimeTzConverter.GetByteCount, TimeTzConverter.Write, TimeTzConverter.Read,
            NullableTimeTzArrayConverter.GetByteCount, NullableTimeTzArrayConverter.Write, NullableTimeTzArrayConverter.Read);
        Value<PgTimestamp>(connection, TypeOid.Timestamp, "timestamp", "'1999-12-31 23:59:59.999999'::timestamp", new PgTimestamp(-1),
            TimestampConverter.GetByteCount, TimestampConverter.Write, TimestampConverter.Read,
            NullableTimestampArrayConverter.GetByteCount, NullableTimestampArrayConverter.Write, NullableTimestampArrayConverter.Read);
        Value<PgTimestampTz>(connection, TypeOid.TimestampTz, "timestamptz", "'2000-01-01 00:00:01.000001+00'::timestamptz", new PgTimestampTz(1000001),
            TimestampTzConverter.GetByteCount, TimestampTzConverter.Write, TimestampTzConverter.Read,
            NullableTimestampTzArrayConverter.GetByteCount, NullableTimestampTzArrayConverter.Write, NullableTimestampTzArrayConverter.Read);
        Value<PgInterval>(connection, TypeOid.Interval, "interval", "'-2 mons 3 days -00:00:01.000001'::interval", new PgInterval(-2, 3, -1000001),
            IntervalConverter.GetByteCount, IntervalConverter.Write, IntervalConverter.Read,
            NullableIntervalArrayConverter.GetByteCount, NullableIntervalArrayConverter.Write, NullableIntervalArrayConverter.Read);
        Value<ReadOnlyMemory<byte>>(connection, TypeOid.Bytea, "bytea", "decode('000180ff','hex')", new ReadOnlyMemory<byte>(new byte[] {0, 1, 128, 255}),
            ByteaConverter.GetByteCount, ByteaConverter.Write, ByteaConverter.Read,
            NullableByteaArrayConverter.GetByteCount, NullableByteaArrayConverter.Write, NullableByteaArrayConverter.Read);
        Value<PgNumeric>(connection, TypeOid.Numeric, "numeric", "-12345.67::numeric", new PgNumeric(1, 2, PgNumericSign.Negative, new ushort[] {1, 2345, 6700}),
            NumericConverter.GetByteCount, NumericConverter.Write, NumericConverter.Read,
            NullableNumericArrayConverter.GetByteCount, NullableNumericArrayConverter.Write, NullableNumericArrayConverter.Read);
        Value<PgInet>(connection, TypeOid.Inet, "inet", "'192.0.2.129/24'::inet", PgInet.FromIPAddress(IPAddress.Parse("192.0.2.129"), 24),
            InetConverter.GetByteCount, InetConverter.Write, InetConverter.Read,
            NullableInetArrayConverter.GetByteCount, NullableInetArrayConverter.Write, NullableInetArrayConverter.Read);
        Value<PgInet>(connection, TypeOid.Cidr, "cidr", "'192.0.2.0/24'::cidr", PgInet.FromIPAddress(IPAddress.Parse("192.0.2.0"), 24),
            CidrConverter.GetByteCount, CidrConverter.Write, CidrConverter.Read,
            NullableCidrArrayConverter.GetByteCount, NullableCidrArrayConverter.Write, NullableCidrArrayConverter.Read);
        ReferenceValue(connection, TypeOid.Text, "text", "'Я😀'::text", "Я😀",
            TextConverter.GetByteCount, TextConverter.Write, TextConverter.Read,
            TextArrayConverter.GetByteCount, TextArrayConverter.Write, TextArrayConverter.Read);
        ReferenceValue(connection, TypeOid.VarChar, "varchar", "'Я😀'::varchar", "Я😀",
            VarCharConverter.GetByteCount, VarCharConverter.Write, VarCharConverter.Read,
            VarCharArrayConverter.GetByteCount, VarCharArrayConverter.Write, VarCharArrayConverter.Read);
        ReferenceValue(connection, TypeOid.BpChar, "bpchar", "'Я😀  '::bpchar", "Я😀  ",
            BpCharConverter.GetByteCount, BpCharConverter.Write, BpCharConverter.Read,
            BpCharArrayConverter.GetByteCount, BpCharArrayConverter.Write, BpCharArrayConverter.Read);
        ReferenceValue(connection, TypeOid.Name, "name", "'Я😀'::name", "Я😀",
            NameConverter.GetByteCount, NameConverter.Write, NameConverter.Read,
            NameArrayConverter.GetByteCount, NameArrayConverter.Write, NameArrayConverter.Read);
        ReferenceValue(connection, TypeOid.Json, "json", "'{\"x\":\"Я😀\"}'::json", "{\"x\":\"Я😀\"}",
            JsonConverter.GetByteCount, JsonConverter.Write, JsonConverter.Read,
            JsonArrayConverter.GetByteCount, JsonArrayConverter.Write, JsonArrayConverter.Read);
        Value<Memory<byte>>(connection, TypeOid.Jsonb, "jsonb", "'{\"x\": \"Я😀\"}'::jsonb", "{\"x\": \"Я😀\"}"u8.ToArray(),
            JsonbConverter.GetByteCount, JsonbConverter.Write, JsonbConverter.Read,
            NullableJsonbArrayConverter.GetByteCount, NullableJsonbArrayConverter.Write, NullableJsonbArrayConverter.Read);
        ReferenceValue(connection, TypeOid.Xml, "xml", "'<a>Я😀</a>'::xml", "<a>Я😀</a>",
            XmlConverter.GetByteCount, XmlConverter.Write, XmlConverter.Read,
            XmlArrayConverter.GetByteCount, XmlArrayConverter.Write, XmlArrayConverter.Read);

        Value<PgInet>(connection, TypeOid.Inet, "inet", "'2001:db8::1234/48'::inet", PgInet.FromIPAddress(IPAddress.Parse("2001:db8::1234"), 48),
            InetConverter.GetByteCount, InetConverter.Write, InetConverter.Read, NullableInetArrayConverter.GetByteCount, NullableInetArrayConverter.Write, NullableInetArrayConverter.Read);
        Value<PgInet>(connection, TypeOid.Cidr, "cidr", "'2001:db8::/48'::cidr", PgInet.FromIPAddress(IPAddress.Parse("2001:db8::"), 48),
            CidrConverter.GetByteCount, CidrConverter.Write, CidrConverter.Read, NullableCidrArrayConverter.GetByteCount, NullableCidrArrayConverter.Write, NullableCidrArrayConverter.Read);
        Value<PgDate>(connection, TypeOid.Date, "date", "'-infinity'::date", PgDate.NegativeInfinity,
            DateConverter.GetByteCount, DateConverter.Write, DateConverter.Read, NullableDateArrayConverter.GetByteCount, NullableDateArrayConverter.Write, NullableDateArrayConverter.Read);
        Value<PgDate>(connection, TypeOid.Date, "date", "'infinity'::date", PgDate.PositiveInfinity,
            DateConverter.GetByteCount, DateConverter.Write, DateConverter.Read, NullableDateArrayConverter.GetByteCount, NullableDateArrayConverter.Write, NullableDateArrayConverter.Read);
        Value<PgTime>(connection, TypeOid.Time, "time", "'24:00:00'::time", new PgTime(PgTime.MicrosecondsPerDay),
            TimeConverter.GetByteCount, TimeConverter.Write, TimeConverter.Read, NullableTimeArrayConverter.GetByteCount, NullableTimeArrayConverter.Write, NullableTimeArrayConverter.Read);
        Value<PgTimestamp>(connection, TypeOid.Timestamp, "timestamp", "'infinity'::timestamp", PgTimestamp.PositiveInfinity,
            TimestampConverter.GetByteCount, TimestampConverter.Write, TimestampConverter.Read, NullableTimestampArrayConverter.GetByteCount, NullableTimestampArrayConverter.Write, NullableTimestampArrayConverter.Read);
        Value<PgTimestampTz>(connection, TypeOid.TimestampTz, "timestamptz", "'-infinity'::timestamptz", PgTimestampTz.NegativeInfinity,
            TimestampTzConverter.GetByteCount, TimestampTzConverter.Write, TimestampTzConverter.Read, NullableTimestampTzArrayConverter.GetByteCount, NullableTimestampTzArrayConverter.Write,
            NullableTimestampTzArrayConverter.Read);
        Value<PgInterval>(connection, TypeOid.Interval, "interval", "'infinity'::interval", PgInterval.PositiveInfinity,
            IntervalConverter.GetByteCount, IntervalConverter.Write, IntervalConverter.Read, NullableIntervalArrayConverter.GetByteCount, NullableIntervalArrayConverter.Write, NullableIntervalArrayConverter.Read);
        Value<PgNumeric>(connection, TypeOid.Numeric, "numeric", "'NaN'::numeric", PgNumeric.NaN,
            NumericConverter.GetByteCount, NumericConverter.Write, NumericConverter.Read, NullableNumericArrayConverter.GetByteCount, NullableNumericArrayConverter.Write, NullableNumericArrayConverter.Read);
        Value<PgNumeric>(connection, TypeOid.Numeric, "numeric", "'infinity'::numeric", PgNumeric.PositiveInfinity,
            NumericConverter.GetByteCount, NumericConverter.Write, NumericConverter.Read, NullableNumericArrayConverter.GetByteCount, NullableNumericArrayConverter.Write, NullableNumericArrayConverter.Read);
        Value<PgNumeric>(connection, TypeOid.Numeric, "numeric", "'-infinity'::numeric", PgNumeric.NegativeInfinity,
            NumericConverter.GetByteCount, NumericConverter.Write, NumericConverter.Read, NullableNumericArrayConverter.GetByteCount, NullableNumericArrayConverter.Write, NullableNumericArrayConverter.Read);
    }
}
