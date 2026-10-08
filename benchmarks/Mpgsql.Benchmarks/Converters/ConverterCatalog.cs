using System.Buffers;
using System.Net;
using System.Numerics;
using System.Text;
using Mpgsql.Converters;
using Mpgsql.Types;
using NpgsqlTypes;

namespace Mpgsql.Benchmarks.Converters;

internal enum ConverterShape { Scalar, Array, NullableArray }

internal sealed record ConverterProfile(string Id, string PostgreSqlType, string Representation,
    ConverterShape Shape, Type ConverterType, Func<int, int, ConverterCase> Create);

internal static class ConverterCatalog
{
    internal static IReadOnlyList<ConverterProfile> Profiles { get; } = Build();
    internal static IEnumerable<string> Ids(ConverterShape shape) => Profiles.Where(p => p.Shape == shape).Select(p => p.Id);
    internal static ConverterProfile Get(string id) => Profiles.Single(p => p.Id == id);

    private static List<ConverterProfile> Build()
    {
        var profiles = new List<ConverterProfile>();

        void Value<TM, TN>(string id, string pg, Type scalar, Type array, Type nullable,
            Func<int, TM> sample, Func<TM, TN> map, string representation,
            Action<TM, IBufferWriter<byte>> write, Func<ReadOnlySequence<byte>, TM> read,
            Action<ReadOnlyMemory<TM>, IBufferWriter<byte>> writeArray, Func<ReadOnlySequence<byte>, ReadOnlyMemory<TM>> readArray,
            Action<ReadOnlyMemory<TM?>, IBufferWriter<byte>> writeNullable, Func<ReadOnlySequence<byte>, ReadOnlyMemory<TM?>> readNullable)
            where TM : struct where TN : struct
        {
            AddScalar(id, pg, scalar, sample, map, representation, write, read);
            AddArray(id, pg, array, sample, map, representation, writeArray, readArray);
            AddArray<TM?, TN?>(id + ".null", pg, nullable, i => sample(i), v => v is { } value ? map(value) : null,
                representation, writeNullable, readNullable, nulls: true);
        }

        void AddScalar<TM, TN>(string id, string pg, Type converter, Func<int, TM> sample, Func<TM, TN> map,
            string representation, Action<TM, IBufferWriter<byte>> write, Func<ReadOnlySequence<byte>, TM> read)
        {
            uint oid = (uint)converter.GetField("TypeOid")!.GetRawConstantValue()!;
            profiles.Add(new(id, pg, representation, ConverterShape.Scalar, converter, (_, _) =>
            {
                TM value = sample(3);
                return new ConverterCase<TM, TN>(id, oid, value, map(value), write, read, array: false);
            }));
        }

        void AddArray<TM, TN>(string id, string pg, Type converter, Func<int, TM> sample, Func<TM, TN> map,
            string representation, Action<ReadOnlyMemory<TM>, IBufferWriter<byte>> write,
            Func<ReadOnlySequence<byte>, ReadOnlyMemory<TM>> read, bool nulls = false)
        {
            uint oid = (uint)converter.GetField("ArrayTypeOid")!.GetRawConstantValue()!;
            string key = nulls ? id : id + ".array";
            profiles.Add(new(key, pg + "[]", representation, nulls ? ConverterShape.NullableArray : ConverterShape.Array,
                converter, (count, nullEvery) =>
                {
                    TM[] values = new TM[count];
                    TN[] upstream = new TN[count];
                    for (int i = 0; i < count; i++)
                    {
                        values[i] = nulls && nullEvery > 0 && i % nullEvery == 0 ? default! : sample(i);
                        upstream[i] = map(values[i]);
                    }
                    return new ConverterCase<ReadOnlyMemory<TM>, TN[]>(key, oid, values, upstream, write, read, array: true);
                }));
        }

        Value<bool, bool>("bool", "bool", typeof(BooleanConverter), typeof(BooleanArrayConverter), typeof(NullableBooleanArrayConverter),
            i => i % 2 == 0, v => v, "bool", BooleanConverter.Write, BooleanConverter.Read, BooleanArrayConverter.Write,
            BooleanArrayConverter.Read, NullableBooleanArrayConverter.Write, NullableBooleanArrayConverter.Read);
        Value<short, short>("int2", "int2", typeof(Int16Converter), typeof(Int16ArrayConverter), typeof(NullableInt16ArrayConverter),
            i => (short)(i % 30000 - 15000), v => v, "short", Int16Converter.Write, Int16Converter.Read, Int16ArrayConverter.Write,
            Int16ArrayConverter.Read, NullableInt16ArrayConverter.Write, NullableInt16ArrayConverter.Read);
        Value<int, int>("int4", "int4", typeof(Int32Converter), typeof(Int32ArrayConverter), typeof(NullableInt32ArrayConverter),
            i => (i & 1) == 0 ? int.MaxValue - i : int.MinValue + i, v => v, "int", Int32Converter.Write, Int32Converter.Read,
            Int32ArrayConverter.Write, Int32ArrayConverter.Read, NullableInt32ArrayConverter.Write, NullableInt32ArrayConverter.Read);
        Value<long, long>("int8", "int8", typeof(Int64Converter), typeof(Int64ArrayConverter), typeof(NullableInt64ArrayConverter),
            i => (i & 1) == 0 ? long.MaxValue - i : long.MinValue + i, v => v, "long", Int64Converter.Write, Int64Converter.Read,
            Int64ArrayConverter.Write, Int64ArrayConverter.Read, NullableInt64ArrayConverter.Write, NullableInt64ArrayConverter.Read);
        Value<float, float>("float4", "float4", typeof(Float32Converter), typeof(Float32ArrayConverter), typeof(NullableFloat32ArrayConverter),
            i => (i - 128) * 1.125f, v => v, "float", Float32Converter.Write, Float32Converter.Read,
            Float32ArrayConverter.Write, Float32ArrayConverter.Read, NullableFloat32ArrayConverter.Write, NullableFloat32ArrayConverter.Read);
        Value<double, double>("float8", "float8", typeof(Float64Converter), typeof(Float64ArrayConverter), typeof(NullableFloat64ArrayConverter),
            i => (i - 128) * 1.125, v => v, "double", Float64Converter.Write, Float64Converter.Read,
            Float64ArrayConverter.Write, Float64ArrayConverter.Read, NullableFloat64ArrayConverter.Write, NullableFloat64ArrayConverter.Read);
        Value<uint, uint>("oid", "oid", typeof(OidConverter), typeof(OidArrayConverter), typeof(NullableOidArrayConverter),
            i => uint.MaxValue - (uint)i, v => v, "uint", OidConverter.Write, OidConverter.Read,
            OidArrayConverter.Write, OidArrayConverter.Read, NullableOidArrayConverter.Write, NullableOidArrayConverter.Read);
        Value<long, decimal>("money", "money", typeof(MoneyConverter), typeof(MoneyArrayConverter), typeof(NullableMoneyArrayConverter),
            i => -123456789L + i, v => v / 100m, "long cents / decimal", MoneyConverter.Write, MoneyConverter.Read,
            MoneyArrayConverter.Write, MoneyArrayConverter.Read, NullableMoneyArrayConverter.Write, NullableMoneyArrayConverter.Read);
        Value<Guid, Guid>("uuid", "uuid", typeof(UuidConverter), typeof(UuidArrayConverter), typeof(NullableUuidArrayConverter),
            i => new Guid(i, 0x1234, 0x5678, 0x90, 0xab, 0xcd, 0xef, 0x12, 0x34, 0x56, 0x78), v => v, "Guid",
            UuidConverter.Write, UuidConverter.Read, UuidArrayConverter.Write, UuidArrayConverter.Read,
            NullableUuidArrayConverter.Write, NullableUuidArrayConverter.Read);
        Value<PgNumeric, decimal>("numeric.pg", "numeric", typeof(NumericConverter), typeof(NumericArrayConverter), typeof(NullableNumericArrayConverter),
            i => PgNumeric.FromDecimal(123456789.1234m + i), v => v.ToDecimal(), "PgNumeric / decimal",
            NumericConverter.Write, NumericConverter.Read, NumericArrayConverter.Write, NumericArrayConverter.Read,
            NullableNumericArrayConverter.Write, NullableNumericArrayConverter.Read);
        Value<decimal, decimal>("numeric.decimal", "numeric", typeof(NumericConverter), typeof(NumericArrayConverter), typeof(NullableNumericArrayConverter),
            i => 123456789.1234m + i, v => v, "decimal", NumericConverter.Write, NumericConverter.ReadDecimal,
            NumericArrayConverter.Write, NumericArrayConverter.ReadDecimals, NullableNumericArrayConverter.Write, NullableNumericArrayConverter.ReadDecimals);
        Value<PgNumeric, BigInteger>("numeric.wide", "numeric", typeof(NumericConverter), typeof(NumericArrayConverter), typeof(NullableNumericArrayConverter),
            WideNumeric, ToBigInteger, "PgNumeric / BigInteger (128 digits)", NumericConverter.Write, NumericConverter.Read,
            NumericArrayConverter.Write, NumericArrayConverter.Read, NullableNumericArrayConverter.Write, NullableNumericArrayConverter.Read);
        Value<PgDate, int>("date.pg", "date", typeof(DateConverter), typeof(DateArrayConverter), typeof(NullableDateArrayConverter),
            i => new PgDate(i - 128), v => v.DaysSinceEpoch, "PgDate / int days", DateConverter.Write, DateConverter.Read,
            DateArrayConverter.Write, DateArrayConverter.Read, NullableDateArrayConverter.Write, NullableDateArrayConverter.Read);
        Value<DateOnly, DateOnly>("date.clr", "date", typeof(DateConverter), typeof(DateArrayConverter), typeof(NullableDateArrayConverter),
            i => new DateOnly(2026, 1, 1).AddDays(i), v => v, "DateOnly", DateConverter.Write, DateConverter.ReadDateOnly,
            DateArrayConverter.Write, DateArrayConverter.ReadDateOnlys, NullableDateArrayConverter.Write, NullableDateArrayConverter.ReadDateOnlys);
        Value<PgTime, long>("time.pg", "time", typeof(TimeConverter), typeof(TimeArrayConverter), typeof(NullableTimeArrayConverter),
            i => new PgTime(12345678900L + i), v => v.Microseconds, "PgTime / long microseconds", TimeConverter.Write, TimeConverter.Read,
            TimeArrayConverter.Write, TimeArrayConverter.Read, NullableTimeArrayConverter.Write, NullableTimeArrayConverter.Read);
        Value<TimeOnly, TimeOnly>("time.clr", "time", typeof(TimeConverter), typeof(TimeArrayConverter), typeof(NullableTimeArrayConverter),
            i => new TimeOnly(123456789000L + i * 10L), v => v, "TimeOnly", TimeConverter.Write, TimeConverter.ReadTimeOnly,
            TimeArrayConverter.Write, TimeArrayConverter.ReadTimeOnlys, NullableTimeArrayConverter.Write, NullableTimeArrayConverter.ReadTimeOnlys);
        Value<PgTimeTz, DateTimeOffset>("timetz", "timetz", typeof(TimeTzConverter), typeof(TimeTzArrayConverter), typeof(NullableTimeTzArrayConverter),
            i => new PgTimeTz(new PgTime(12345678900L + i), 18000),
            v => new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.FromSeconds(v.OffsetSeconds)).AddTicks(v.Time.Microseconds * 10),
            "PgTimeTz / DateTimeOffset", TimeTzConverter.Write, TimeTzConverter.Read,
            TimeTzArrayConverter.Write, TimeTzArrayConverter.Read, NullableTimeTzArrayConverter.Write, NullableTimeTzArrayConverter.Read);
        Value<PgTimestamp, long>("timestamp.pg", "timestamp", typeof(TimestampConverter), typeof(TimestampArrayConverter), typeof(NullableTimestampArrayConverter),
            i => new PgTimestamp(123456789000000L + i), v => v.MicrosecondsSinceEpoch, "PgTimestamp / long microseconds",
            TimestampConverter.Write, TimestampConverter.Read, TimestampArrayConverter.Write, TimestampArrayConverter.Read,
            NullableTimestampArrayConverter.Write, NullableTimestampArrayConverter.Read);
        Value<DateTime, DateTime>("timestamp.clr", "timestamp", typeof(TimestampConverter), typeof(TimestampArrayConverter), typeof(NullableTimestampArrayConverter),
            i => new DateTime(2026, 1, 1, 12, 34, 56, DateTimeKind.Unspecified).AddTicks(i * 10L), v => v, "DateTime Unspecified",
            TimestampConverter.Write, TimestampConverter.ReadDateTime, TimestampArrayConverter.Write, TimestampArrayConverter.ReadDateTimes,
            NullableTimestampArrayConverter.Write, NullableTimestampArrayConverter.ReadDateTimes);
        Value<PgTimestampTz, long>("timestamptz.pg", "timestamptz", typeof(TimestampTzConverter), typeof(TimestampTzArrayConverter), typeof(NullableTimestampTzArrayConverter),
            i => new PgTimestampTz(123456789000000L + i), v => v.MicrosecondsSinceEpoch, "PgTimestampTz / long microseconds",
            TimestampTzConverter.Write, TimestampTzConverter.Read, TimestampTzArrayConverter.Write, TimestampTzArrayConverter.Read,
            NullableTimestampTzArrayConverter.Write, NullableTimestampTzArrayConverter.Read);
        Value<DateTimeOffset, DateTimeOffset>("timestamptz.clr", "timestamptz", typeof(TimestampTzConverter), typeof(TimestampTzArrayConverter), typeof(NullableTimestampTzArrayConverter),
            i => new DateTimeOffset(2026, 1, 1, 12, 34, 56, TimeSpan.Zero).AddTicks(i * 10L), v => v, "DateTimeOffset UTC",
            TimestampTzConverter.Write, TimestampTzConverter.ReadDateTimeOffset, TimestampTzArrayConverter.Write, TimestampTzArrayConverter.ReadDateTimeOffsets,
            NullableTimestampTzArrayConverter.Write, NullableTimestampTzArrayConverter.ReadDateTimeOffsets);
        AddScalar<DateTime, DateTime>("timestamptz.datetime", "timestamptz", typeof(TimestampTzConverter),
            i => new DateTime(2026, 1, 1, 12, 34, 56, DateTimeKind.Utc).AddTicks(i * 10L), v => v, "DateTime UTC",
            TimestampTzConverter.Write, TimestampTzConverter.ReadDateTime);
        Value<PgInterval, NpgsqlInterval>("interval.pg", "interval", typeof(IntervalConverter), typeof(IntervalArrayConverter), typeof(NullableIntervalArrayConverter),
            i => new PgInterval(3, 7, 123456789L + i), v => new NpgsqlInterval(v.Months, v.Days, v.Microseconds), "PgInterval / NpgsqlInterval",
            IntervalConverter.Write, IntervalConverter.Read, IntervalArrayConverter.Write, IntervalArrayConverter.Read,
            NullableIntervalArrayConverter.Write, NullableIntervalArrayConverter.Read);
        Value<TimeSpan, TimeSpan>("interval.clr", "interval", typeof(IntervalConverter), typeof(IntervalArrayConverter), typeof(NullableIntervalArrayConverter),
            i => new TimeSpan(3 * TimeSpan.TicksPerDay + 1234567890L + i * 10L), v => v, "TimeSpan",
            IntervalConverter.Write, IntervalConverter.ReadTimeSpan, IntervalArrayConverter.Write, IntervalArrayConverter.ReadTimeSpans,
            NullableIntervalArrayConverter.Write, NullableIntervalArrayConverter.ReadTimeSpans);
        Value<PgInet, NpgsqlInet>("inet", "inet", typeof(InetConverter), typeof(InetArrayConverter), typeof(NullableInetArrayConverter),
            i => PgInet.FromIPAddress(IPAddress.Parse((i & 1) == 0 ? "192.168.1.42" : "2001:db8::1234"), (i & 1) == 0 ? 24 : 64),
            v => new NpgsqlInet(v.ToIPAddress(), v.PrefixLength), "PgInet / NpgsqlInet (IPv4 + IPv6)",
            InetConverter.Write, InetConverter.Read, InetArrayConverter.Write, InetArrayConverter.Read, NullableInetArrayConverter.Write, NullableInetArrayConverter.Read);
        Value<PgInet, IPNetwork>("cidr", "cidr", typeof(CidrConverter), typeof(CidrArrayConverter), typeof(NullableCidrArrayConverter),
            i => PgInet.FromIPAddress(IPAddress.Parse((i & 1) == 0 ? "192.168.1.0" : "2001:db8::"), (i & 1) == 0 ? 24 : 64),
            v => new IPNetwork(v.ToIPAddress(), v.PrefixLength), "PgInet / IPNetwork (IPv4 + IPv6)",
            CidrConverter.Write, CidrConverter.Read, CidrArrayConverter.Write, CidrArrayConverter.Read, NullableCidrArrayConverter.Write, NullableCidrArrayConverter.Read);
        Value<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>>("bytea", "bytea", typeof(ByteaConverter), typeof(ByteaArrayConverter), typeof(NullableByteaArrayConverter),
            i => Bytes(i), v => v, "ReadOnlyMemory<byte> (64 B)", ByteaConverter.Write, ByteaConverter.Read,
            ByteaArrayConverter.Write, ByteaArrayConverter.Read, NullableByteaArrayConverter.Write, NullableByteaArrayConverter.Read);
        AddArray<byte[]?, byte[]?>("bytea.bytes", "bytea", typeof(ByteaArrayConverter), Bytes, v => v, "byte[] (64 B)",
            ByteaArrayConverter.Write, ByteaArrayConverter.ReadByteArrays);
        AddArray<byte[]?, byte[]?>("bytea.bytes.null", "bytea", typeof(ByteaArrayConverter), Bytes, v => v, "byte[] (64 B)",
            ByteaArrayConverter.Write, ByteaArrayConverter.ReadByteArrays, nulls: true);

        AddScalar<Memory<byte>, byte[]>("jsonb", "jsonb", typeof(JsonbConverter), i => Encoding.UTF8.GetBytes(Json(i)), v => v.ToArray(),
            "Memory<byte> / byte[] UTF-8", JsonbConverter.Write, JsonbConverter.Read);
        AddArray<Memory<byte>, byte[]>("jsonb", "jsonb", typeof(JsonbArrayConverter), i => Encoding.UTF8.GetBytes(Json(i)), v => v.ToArray(),
            "Memory<byte> / byte[] UTF-8", JsonbArrayConverter.Write, JsonbArrayConverter.Read);
        AddArray<Memory<byte>?, byte[]?>("jsonb.null", "jsonb", typeof(NullableJsonbArrayConverter), i => Encoding.UTF8.GetBytes(Json(i)),
            v => v?.ToArray(), "Memory<byte> / byte[] UTF-8", NullableJsonbArrayConverter.Write, NullableJsonbArrayConverter.Read, nulls: true);

        void Text(string id, string pg, Type scalar, Type array, Func<int, string> sample,
            Action<string, IBufferWriter<byte>> write, Func<ReadOnlySequence<byte>, string> read,
            Action<ReadOnlyMemory<string?>, IBufferWriter<byte>> writeArray, Func<ReadOnlySequence<byte>, ReadOnlyMemory<string?>> readArray)
        {
            AddScalar(id, pg, scalar, sample, v => v, "string UTF-8", write, read);
            AddArray<string?, string?>(id, pg, array, sample, v => v, "string UTF-8", writeArray, readArray);
            AddArray<string?, string?>(id + ".null", pg, array, sample, v => v, "string UTF-8", writeArray, readArray, nulls: true);
        }
        Text("text", "text", typeof(TextConverter), typeof(TextArrayConverter), TextValue,
            TextConverter.Write, TextConverter.Read, TextArrayConverter.Write, TextArrayConverter.Read);
        Text("varchar", "varchar", typeof(VarCharConverter), typeof(VarCharArrayConverter), TextValue,
            VarCharConverter.Write, VarCharConverter.Read, VarCharArrayConverter.Write, VarCharArrayConverter.Read);
        Text("bpchar", "bpchar", typeof(BpCharConverter), typeof(BpCharArrayConverter), i => TextValue(i) + "   ",
            BpCharConverter.Write, BpCharConverter.Read, BpCharArrayConverter.Write, BpCharArrayConverter.Read);
        Text("name", "name", typeof(NameConverter), typeof(NameArrayConverter), i => $"column_данные_{i:D4}",
            NameConverter.Write, NameConverter.Read, NameArrayConverter.Write, NameArrayConverter.Read);
        Text("json", "json", typeof(JsonConverter), typeof(JsonArrayConverter), Json,
            JsonConverter.Write, JsonConverter.Read, JsonArrayConverter.Write, JsonArrayConverter.Read);
        Text("xml", "xml", typeof(XmlConverter), typeof(XmlArrayConverter), i => $"<row id=\"{i:D4}\"><value>данные UTF-8</value></row>",
            XmlConverter.Write, XmlConverter.Read, XmlArrayConverter.Write, XmlArrayConverter.Read);
        return profiles;
    }

    private static string TextValue(int i) => $"row_{i:D4}: данные UTF-8, abcdefghijklmnopqrstuvwxyz";
    private static string Json(int i) => $"{{\"id\":{i},\"value\":\"данные UTF-8 abcdefghijklmnopqrstuvwxyz\"}}";
    private static byte[] Bytes(int i) => Enumerable.Range(0, 64).Select(j => (byte)(j + i)).ToArray();
    private static PgNumeric WideNumeric(int i)
        => new(127, 0, (i & 1) == 0 ? PgNumericSign.Positive : PgNumericSign.Negative,
            Enumerable.Range(0, 128).Select(j => (ushort)(1234 + (j + i) % 8000)).ToArray());
    private static BigInteger ToBigInteger(PgNumeric value)
    {
        BigInteger result = 0;
        foreach (ushort digit in value.Digits.Span) result = result * 10000 + digit;
        return value.Sign == PgNumericSign.Negative ? -result : result;
    }
}
