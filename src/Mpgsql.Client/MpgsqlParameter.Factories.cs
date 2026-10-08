using Mpgsql.Types;

namespace Mpgsql;

public partial class MpgsqlParameter
{
    public static MpgsqlParameter<long?> Int64(long? value)
    {
        return new MpgsqlParameter<long?>(TypeOid.Int64, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<long>?> Int64Array(ReadOnlyMemory<long>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<long>?>(TypeOid.Int64Array, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<long?>?> NullableInt64Array(ReadOnlyMemory<long?>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<long?>?>(TypeOid.Int64Array, value);
    }
    public static MpgsqlParameter<bool?> Boolean(bool? value)
    {
        return new MpgsqlParameter<bool?>(TypeOid.Boolean, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<bool>?> BooleanArray(ReadOnlyMemory<bool>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<bool>?>(TypeOid.BooleanArray, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<bool?>?> NullableBooleanArray(ReadOnlyMemory<bool?>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<bool?>?>(TypeOid.BooleanArray, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<byte>?> Bytea(ReadOnlyMemory<byte>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<byte>?>(TypeOid.Bytea, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<ReadOnlyMemory<byte>>?> ByteaArray(ReadOnlyMemory<ReadOnlyMemory<byte>>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<ReadOnlyMemory<byte>>?>(TypeOid.ByteaArray, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<ReadOnlyMemory<byte>?>?> NullableByteaArray(ReadOnlyMemory<ReadOnlyMemory<byte>?>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<ReadOnlyMemory<byte>?>?>(TypeOid.ByteaArray, value);
    }
    public static MpgsqlParameter<short?> Int16(short? value)
    {
        return new MpgsqlParameter<short?>(TypeOid.Int16, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<short>?> Int16Array(ReadOnlyMemory<short>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<short>?>(TypeOid.Int16Array, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<short?>?> NullableInt16Array(ReadOnlyMemory<short?>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<short?>?>(TypeOid.Int16Array, value);
    }
    public static MpgsqlParameter<int?> Int32(int? value)
    {
        return new MpgsqlParameter<int?>(TypeOid.Int32, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<int>?> Int32Array(ReadOnlyMemory<int>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<int>?>(TypeOid.Int32Array, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<int?>?> NullableInt32Array(ReadOnlyMemory<int?>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<int?>?>(TypeOid.Int32Array, value);
    }
    public static MpgsqlParameter<float?> Float32(float? value)
    {
        return new MpgsqlParameter<float?>(TypeOid.Float32, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<float>?> Float32Array(ReadOnlyMemory<float>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<float>?>(TypeOid.Float32Array, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<float?>?> NullableFloat32Array(ReadOnlyMemory<float?>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<float?>?>(TypeOid.Float32Array, value);
    }
    public static MpgsqlParameter<double?> Float64(double? value)
    {
        return new MpgsqlParameter<double?>(TypeOid.Float64, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<double>?> Float64Array(ReadOnlyMemory<double>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<double>?>(TypeOid.Float64Array, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<double?>?> NullableFloat64Array(ReadOnlyMemory<double?>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<double?>?>(TypeOid.Float64Array, value);
    }
    public static MpgsqlParameter<PgNumeric?> Numeric(PgNumeric? value)
    {
        return new MpgsqlParameter<PgNumeric?>(TypeOid.Numeric, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<PgNumeric>?> NumericArray(ReadOnlyMemory<PgNumeric>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<PgNumeric>?>(TypeOid.NumericArray, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<PgNumeric?>?> NullableNumericArray(ReadOnlyMemory<PgNumeric?>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<PgNumeric?>?>(TypeOid.NumericArray, value);
    }
    public static MpgsqlParameter<long?> Money(long? value)
    {
        return new MpgsqlParameter<long?>(TypeOid.Money, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<long>?> MoneyArray(ReadOnlyMemory<long>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<long>?>(TypeOid.MoneyArray, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<long?>?> NullableMoneyArray(ReadOnlyMemory<long?>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<long?>?>(TypeOid.MoneyArray, value);
    }
    public static MpgsqlParameter<string?> Text(string? value)
    {
        return new MpgsqlParameter<string?>(TypeOid.Text, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<string?>?> TextArray(ReadOnlyMemory<string?>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<string?>?>(TypeOid.TextArray, value);
    }
    public static MpgsqlParameter<string?> VarChar(string? value)
    {
        return new MpgsqlParameter<string?>(TypeOid.VarChar, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<string?>?> VarCharArray(ReadOnlyMemory<string?>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<string?>?>(TypeOid.VarCharArray, value);
    }
    public static MpgsqlParameter<string?> BpChar(string? value)
    {
        return new MpgsqlParameter<string?>(TypeOid.BpChar, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<string?>?> BpCharArray(ReadOnlyMemory<string?>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<string?>?>(TypeOid.BpCharArray, value);
    }
    public static MpgsqlParameter<string?> Name(string? value)
    {
        return new MpgsqlParameter<string?>(TypeOid.Name, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<string?>?> NameArray(ReadOnlyMemory<string?>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<string?>?>(TypeOid.NameArray, value);
    }
    public static MpgsqlParameter<Guid?> Uuid(Guid? value)
    {
        return new MpgsqlParameter<Guid?>(TypeOid.Uuid, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<Guid>?> UuidArray(ReadOnlyMemory<Guid>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<Guid>?>(TypeOid.UuidArray, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<Guid?>?> NullableUuidArray(ReadOnlyMemory<Guid?>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<Guid?>?>(TypeOid.UuidArray, value);
    }
    public static MpgsqlParameter<string?> Json(string? value)
    {
        return new MpgsqlParameter<string?>(TypeOid.Json, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<string?>?> JsonArray(ReadOnlyMemory<string?>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<string?>?>(TypeOid.JsonArray, value);
    }
    public static MpgsqlParameter<Memory<byte>?> Jsonb(Memory<byte>? value)
    {
        return new MpgsqlParameter<Memory<byte>?>(TypeOid.Jsonb, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<Memory<byte>>?> JsonbArray(ReadOnlyMemory<Memory<byte>>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<Memory<byte>>?>(TypeOid.JsonbArray, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<Memory<byte>?>?> NullableJsonbArray(ReadOnlyMemory<Memory<byte>?>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<Memory<byte>?>?>(TypeOid.JsonbArray, value);
    }
    public static MpgsqlParameter<string?> Xml(string? value)
    {
        return new MpgsqlParameter<string?>(TypeOid.Xml, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<string?>?> XmlArray(ReadOnlyMemory<string?>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<string?>?>(TypeOid.XmlArray, value);
    }
    public static MpgsqlParameter<PgDate?> Date(PgDate? value)
    {
        return new MpgsqlParameter<PgDate?>(TypeOid.Date, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<PgDate>?> DateArray(ReadOnlyMemory<PgDate>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<PgDate>?>(TypeOid.DateArray, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<PgDate?>?> NullableDateArray(ReadOnlyMemory<PgDate?>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<PgDate?>?>(TypeOid.DateArray, value);
    }
    public static MpgsqlParameter<PgTime?> Time(PgTime? value)
    {
        return new MpgsqlParameter<PgTime?>(TypeOid.Time, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<PgTime>?> TimeArray(ReadOnlyMemory<PgTime>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<PgTime>?>(TypeOid.TimeArray, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<PgTime?>?> NullableTimeArray(ReadOnlyMemory<PgTime?>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<PgTime?>?>(TypeOid.TimeArray, value);
    }
    public static MpgsqlParameter<PgTimeTz?> TimeTz(PgTimeTz? value)
    {
        return new MpgsqlParameter<PgTimeTz?>(TypeOid.TimeTz, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<PgTimeTz>?> TimeTzArray(ReadOnlyMemory<PgTimeTz>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<PgTimeTz>?>(TypeOid.TimeTzArray, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<PgTimeTz?>?> NullableTimeTzArray(ReadOnlyMemory<PgTimeTz?>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<PgTimeTz?>?>(TypeOid.TimeTzArray, value);
    }
    public static MpgsqlParameter<PgTimestamp?> Timestamp(PgTimestamp? value)
    {
        return new MpgsqlParameter<PgTimestamp?>(TypeOid.Timestamp, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<PgTimestamp>?> TimestampArray(ReadOnlyMemory<PgTimestamp>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<PgTimestamp>?>(TypeOid.TimestampArray, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<PgTimestamp?>?> NullableTimestampArray(ReadOnlyMemory<PgTimestamp?>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<PgTimestamp?>?>(TypeOid.TimestampArray, value);
    }
    public static MpgsqlParameter<PgTimestampTz?> TimestampTz(PgTimestampTz? value)
    {
        return new MpgsqlParameter<PgTimestampTz?>(TypeOid.TimestampTz, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<PgTimestampTz>?> TimestampTzArray(ReadOnlyMemory<PgTimestampTz>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<PgTimestampTz>?>(TypeOid.TimestampTzArray, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<PgTimestampTz?>?> NullableTimestampTzArray(ReadOnlyMemory<PgTimestampTz?>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<PgTimestampTz?>?>(TypeOid.TimestampTzArray, value);
    }
    public static MpgsqlParameter<PgInterval?> Interval(PgInterval? value)
    {
        return new MpgsqlParameter<PgInterval?>(TypeOid.Interval, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<PgInterval>?> IntervalArray(ReadOnlyMemory<PgInterval>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<PgInterval>?>(TypeOid.IntervalArray, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<PgInterval?>?> NullableIntervalArray(ReadOnlyMemory<PgInterval?>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<PgInterval?>?>(TypeOid.IntervalArray, value);
    }
    public static MpgsqlParameter<PgInet?> Inet(PgInet? value)
    {
        return new MpgsqlParameter<PgInet?>(TypeOid.Inet, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<PgInet>?> InetArray(ReadOnlyMemory<PgInet>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<PgInet>?>(TypeOid.InetArray, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<PgInet?>?> NullableInetArray(ReadOnlyMemory<PgInet?>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<PgInet?>?>(TypeOid.InetArray, value);
    }
    public static MpgsqlParameter<PgInet?> Cidr(PgInet? value)
    {
        return new MpgsqlParameter<PgInet?>(TypeOid.Cidr, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<PgInet>?> CidrArray(ReadOnlyMemory<PgInet>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<PgInet>?>(TypeOid.CidrArray, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<PgInet?>?> NullableCidrArray(ReadOnlyMemory<PgInet?>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<PgInet?>?>(TypeOid.CidrArray, value);
    }
    public static MpgsqlParameter<uint?> Oid(uint? value)
    {
        return new MpgsqlParameter<uint?>(TypeOid.Oid, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<uint>?> OidArray(ReadOnlyMemory<uint>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<uint>?>(TypeOid.OidArray, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<uint?>?> NullableOidArray(ReadOnlyMemory<uint?>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<uint?>?>(TypeOid.OidArray, value);
    }
    public static MpgsqlParameter<decimal?> Decimal(decimal? value)
    {
        return new MpgsqlParameter<decimal?>(TypeOid.Numeric, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<decimal>?> DecimalArray(ReadOnlyMemory<decimal>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<decimal>?>(TypeOid.NumericArray, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<decimal?>?> NullableDecimalArray(ReadOnlyMemory<decimal?>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<decimal?>?>(TypeOid.NumericArray, value);
    }
    public static MpgsqlParameter<DateOnly?> DateOnly(DateOnly? value)
    {
        return new MpgsqlParameter<DateOnly?>(TypeOid.Date, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<DateOnly>?> DateOnlyArray(ReadOnlyMemory<DateOnly>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<DateOnly>?>(TypeOid.DateArray, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<DateOnly?>?> NullableDateOnlyArray(ReadOnlyMemory<DateOnly?>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<DateOnly?>?>(TypeOid.DateArray, value);
    }
    public static MpgsqlParameter<TimeOnly?> TimeOnly(TimeOnly? value)
    {
        return new MpgsqlParameter<TimeOnly?>(TypeOid.Time, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<TimeOnly>?> TimeOnlyArray(ReadOnlyMemory<TimeOnly>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<TimeOnly>?>(TypeOid.TimeArray, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<TimeOnly?>?> NullableTimeOnlyArray(ReadOnlyMemory<TimeOnly?>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<TimeOnly?>?>(TypeOid.TimeArray, value);
    }
    public static MpgsqlParameter<DateTime?> DateTime(DateTime? value)
    {
        return new MpgsqlParameter<DateTime?>(TypeOid.Timestamp, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<DateTime>?> DateTimeArray(ReadOnlyMemory<DateTime>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<DateTime>?>(TypeOid.TimestampArray, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<DateTime?>?> NullableDateTimeArray(ReadOnlyMemory<DateTime?>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<DateTime?>?>(TypeOid.TimestampArray, value);
    }
    public static MpgsqlParameter<DateTimeOffset?> DateTimeOffset(DateTimeOffset? value)
    {
        return new MpgsqlParameter<DateTimeOffset?>(TypeOid.TimestampTz, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<DateTimeOffset>?> DateTimeOffsetArray(ReadOnlyMemory<DateTimeOffset>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<DateTimeOffset>?>(TypeOid.TimestampTzArray, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<DateTimeOffset?>?> NullableDateTimeOffsetArray(ReadOnlyMemory<DateTimeOffset?>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<DateTimeOffset?>?>(TypeOid.TimestampTzArray, value);
    }
    public static MpgsqlParameter<TimeSpan?> TimeSpan(TimeSpan? value)
    {
        return new MpgsqlParameter<TimeSpan?>(TypeOid.Interval, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<TimeSpan>?> TimeSpanArray(ReadOnlyMemory<TimeSpan>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<TimeSpan>?>(TypeOid.IntervalArray, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<TimeSpan?>?> NullableTimeSpanArray(ReadOnlyMemory<TimeSpan?>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<TimeSpan?>?>(TypeOid.IntervalArray, value);
    }
    public static MpgsqlParameter<ReadOnlyMemory<byte[]?>?> ByteaByteArrays(ReadOnlyMemory<byte[]?>? value)
    {
        return new MpgsqlParameter<ReadOnlyMemory<byte[]?>?>(TypeOid.ByteaArray, value);
    }
}