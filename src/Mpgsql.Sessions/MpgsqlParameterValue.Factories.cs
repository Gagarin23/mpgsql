using Mpgsql.Converters;
using Mpgsql.Types;

namespace Mpgsql;

public readonly partial struct MpgsqlParameterValue
{
    public static MpgsqlParameterValue Boolean(bool? value)
    {
        return Scalar<bool, BooleanCodec>((uint)TypeOid.Boolean, value);
    }
    public static MpgsqlParameterValue BooleanArray(ReadOnlyMemory<bool>? value)
    {
        return Array<bool, BooleanCodec>((uint)TypeOid.BooleanArray, value);
    }
    public static MpgsqlParameterValue NullableBooleanArray(ReadOnlyMemory<bool?>? value)
    {
        return NullableArray<bool, BooleanCodec>((uint)TypeOid.BooleanArray, value);
    }
    public static MpgsqlParameterValue Bytea(ReadOnlyMemory<byte>? value)
    {
        return Scalar<ReadOnlyMemory<byte>, ByteaCodec>((uint)TypeOid.Bytea, value);
    }
    public static MpgsqlParameterValue ByteaArray(ReadOnlyMemory<ReadOnlyMemory<byte>>? value)
    {
        return Array<ReadOnlyMemory<byte>, ByteaCodec>((uint)TypeOid.ByteaArray, value);
    }
    public static MpgsqlParameterValue NullableByteaArray(ReadOnlyMemory<ReadOnlyMemory<byte>?>? value)
    {
        return NullableArray<ReadOnlyMemory<byte>, ByteaCodec>((uint)TypeOid.ByteaArray, value);
    }
    public static MpgsqlParameterValue Int16(short? value)
    {
        return Scalar<short, Int16Codec>((uint)TypeOid.Int16, value);
    }
    public static MpgsqlParameterValue Int16Array(ReadOnlyMemory<short>? value)
    {
        return Array<short, Int16Codec>((uint)TypeOid.Int16Array, value);
    }
    public static MpgsqlParameterValue NullableInt16Array(ReadOnlyMemory<short?>? value)
    {
        return NullableArray<short, Int16Codec>((uint)TypeOid.Int16Array, value);
    }
    public static MpgsqlParameterValue Int32(int? value)
    {
        return Scalar<int, Int32Codec>((uint)TypeOid.Int32, value);
    }
    public static MpgsqlParameterValue Int32Array(ReadOnlyMemory<int>? value)
    {
        return Array<int, Int32Codec>((uint)TypeOid.Int32Array, value);
    }
    public static MpgsqlParameterValue NullableInt32Array(ReadOnlyMemory<int?>? value)
    {
        return NullableArray<int, Int32Codec>((uint)TypeOid.Int32Array, value);
    }
    public static MpgsqlParameterValue Float32(float? value)
    {
        return Scalar<float, Float32Codec>((uint)TypeOid.Float32, value);
    }
    public static MpgsqlParameterValue Float32Array(ReadOnlyMemory<float>? value)
    {
        return Array<float, Float32Codec>((uint)TypeOid.Float32Array, value);
    }
    public static MpgsqlParameterValue NullableFloat32Array(ReadOnlyMemory<float?>? value)
    {
        return NullableArray<float, Float32Codec>((uint)TypeOid.Float32Array, value);
    }
    public static MpgsqlParameterValue Float64(double? value)
    {
        return Scalar<double, Float64Codec>((uint)TypeOid.Float64, value);
    }
    public static MpgsqlParameterValue Float64Array(ReadOnlyMemory<double>? value)
    {
        return Array<double, Float64Codec>((uint)TypeOid.Float64Array, value);
    }
    public static MpgsqlParameterValue NullableFloat64Array(ReadOnlyMemory<double?>? value)
    {
        return NullableArray<double, Float64Codec>((uint)TypeOid.Float64Array, value);
    }
    public static MpgsqlParameterValue Numeric(PgNumeric? value)
    {
        return Scalar<PgNumeric, NumericCodec>((uint)TypeOid.Numeric, value);
    }
    public static MpgsqlParameterValue NumericArray(ReadOnlyMemory<PgNumeric>? value)
    {
        return Array<PgNumeric, NumericCodec>((uint)TypeOid.NumericArray, value);
    }
    public static MpgsqlParameterValue NullableNumericArray(ReadOnlyMemory<PgNumeric?>? value)
    {
        return NullableArray<PgNumeric, NumericCodec>((uint)TypeOid.NumericArray, value);
    }
    public static MpgsqlParameterValue Money(long? value)
    {
        return Scalar<long, MoneyCodec>((uint)TypeOid.Money, value);
    }
    public static MpgsqlParameterValue MoneyArray(ReadOnlyMemory<long>? value)
    {
        return Array<long, MoneyCodec>((uint)TypeOid.MoneyArray, value);
    }
    public static MpgsqlParameterValue NullableMoneyArray(ReadOnlyMemory<long?>? value)
    {
        return NullableArray<long, MoneyCodec>((uint)TypeOid.MoneyArray, value);
    }
    public static MpgsqlParameterValue Text(string? value)
    {
        return Reference<string, TextCodec>((uint)TypeOid.Text, value);
    }
    public static MpgsqlParameterValue TextArray(ReadOnlyMemory<string?>? value)
    {
        return ReferenceArray<string, TextCodec>((uint)TypeOid.TextArray, value);
    }
    public static MpgsqlParameterValue VarChar(string? value)
    {
        return Reference<string, VarCharCodec>((uint)TypeOid.VarChar, value);
    }
    public static MpgsqlParameterValue VarCharArray(ReadOnlyMemory<string?>? value)
    {
        return ReferenceArray<string, VarCharCodec>((uint)TypeOid.VarCharArray, value);
    }
    public static MpgsqlParameterValue BpChar(string? value)
    {
        return Reference<string, BpCharCodec>((uint)TypeOid.BpChar, value);
    }
    public static MpgsqlParameterValue BpCharArray(ReadOnlyMemory<string?>? value)
    {
        return ReferenceArray<string, BpCharCodec>((uint)TypeOid.BpCharArray, value);
    }
    public static MpgsqlParameterValue Name(string? value)
    {
        return Reference<string, NameCodec>((uint)TypeOid.Name, value);
    }
    public static MpgsqlParameterValue NameArray(ReadOnlyMemory<string?>? value)
    {
        return ReferenceArray<string, NameCodec>((uint)TypeOid.NameArray, value);
    }
    public static MpgsqlParameterValue Uuid(Guid? value)
    {
        return Scalar<Guid, UuidCodec>((uint)TypeOid.Uuid, value);
    }
    public static MpgsqlParameterValue UuidArray(ReadOnlyMemory<Guid>? value)
    {
        return Array<Guid, UuidCodec>((uint)TypeOid.UuidArray, value);
    }
    public static MpgsqlParameterValue NullableUuidArray(ReadOnlyMemory<Guid?>? value)
    {
        return NullableArray<Guid, UuidCodec>((uint)TypeOid.UuidArray, value);
    }
    public static MpgsqlParameterValue Json(string? value)
    {
        return Reference<string, JsonCodec>((uint)TypeOid.Json, value);
    }
    public static MpgsqlParameterValue JsonArray(ReadOnlyMemory<string?>? value)
    {
        return ReferenceArray<string, JsonCodec>((uint)TypeOid.JsonArray, value);
    }
    public static MpgsqlParameterValue Jsonb(Memory<byte>? value)
    {
        return Scalar<Memory<byte>, JsonbCodec>((uint)TypeOid.Jsonb, value);
    }
    public static MpgsqlParameterValue JsonbArray(ReadOnlyMemory<Memory<byte>>? value)
    {
        return Array<Memory<byte>, JsonbCodec>((uint)TypeOid.JsonbArray, value);
    }
    public static MpgsqlParameterValue NullableJsonbArray(ReadOnlyMemory<Memory<byte>?>? value)
    {
        return NullableArray<Memory<byte>, JsonbCodec>((uint)TypeOid.JsonbArray, value);
    }
    public static MpgsqlParameterValue Xml(string? value)
    {
        return Reference<string, XmlCodec>((uint)TypeOid.Xml, value);
    }
    public static MpgsqlParameterValue XmlArray(ReadOnlyMemory<string?>? value)
    {
        return ReferenceArray<string, XmlCodec>((uint)TypeOid.XmlArray, value);
    }
    public static MpgsqlParameterValue Date(PgDate? value)
    {
        return Scalar<PgDate, DateCodec>((uint)TypeOid.Date, value);
    }
    public static MpgsqlParameterValue DateArray(ReadOnlyMemory<PgDate>? value)
    {
        return Array<PgDate, DateCodec>((uint)TypeOid.DateArray, value);
    }
    public static MpgsqlParameterValue NullableDateArray(ReadOnlyMemory<PgDate?>? value)
    {
        return NullableArray<PgDate, DateCodec>((uint)TypeOid.DateArray, value);
    }
    public static MpgsqlParameterValue Time(PgTime? value)
    {
        return Scalar<PgTime, TimeCodec>((uint)TypeOid.Time, value);
    }
    public static MpgsqlParameterValue TimeArray(ReadOnlyMemory<PgTime>? value)
    {
        return Array<PgTime, TimeCodec>((uint)TypeOid.TimeArray, value);
    }
    public static MpgsqlParameterValue NullableTimeArray(ReadOnlyMemory<PgTime?>? value)
    {
        return NullableArray<PgTime, TimeCodec>((uint)TypeOid.TimeArray, value);
    }
    public static MpgsqlParameterValue TimeTz(PgTimeTz? value)
    {
        return Scalar<PgTimeTz, TimeTzCodec>((uint)TypeOid.TimeTz, value);
    }
    public static MpgsqlParameterValue TimeTzArray(ReadOnlyMemory<PgTimeTz>? value)
    {
        return Array<PgTimeTz, TimeTzCodec>((uint)TypeOid.TimeTzArray, value);
    }
    public static MpgsqlParameterValue NullableTimeTzArray(ReadOnlyMemory<PgTimeTz?>? value)
    {
        return NullableArray<PgTimeTz, TimeTzCodec>((uint)TypeOid.TimeTzArray, value);
    }
    public static MpgsqlParameterValue Timestamp(PgTimestamp? value)
    {
        return Scalar<PgTimestamp, TimestampCodec>((uint)TypeOid.Timestamp, value);
    }
    public static MpgsqlParameterValue TimestampArray(ReadOnlyMemory<PgTimestamp>? value)
    {
        return Array<PgTimestamp, TimestampCodec>((uint)TypeOid.TimestampArray, value);
    }
    public static MpgsqlParameterValue NullableTimestampArray(ReadOnlyMemory<PgTimestamp?>? value)
    {
        return NullableArray<PgTimestamp, TimestampCodec>((uint)TypeOid.TimestampArray, value);
    }
    public static MpgsqlParameterValue TimestampTz(PgTimestampTz? value)
    {
        return Scalar<PgTimestampTz, TimestampTzCodec>((uint)TypeOid.TimestampTz, value);
    }
    public static MpgsqlParameterValue TimestampTzArray(ReadOnlyMemory<PgTimestampTz>? value)
    {
        return Array<PgTimestampTz, TimestampTzCodec>((uint)TypeOid.TimestampTzArray, value);
    }
    public static MpgsqlParameterValue NullableTimestampTzArray(ReadOnlyMemory<PgTimestampTz?>? value)
    {
        return NullableArray<PgTimestampTz, TimestampTzCodec>((uint)TypeOid.TimestampTzArray, value);
    }
    public static MpgsqlParameterValue Interval(PgInterval? value)
    {
        return Scalar<PgInterval, IntervalCodec>((uint)TypeOid.Interval, value);
    }
    public static MpgsqlParameterValue IntervalArray(ReadOnlyMemory<PgInterval>? value)
    {
        return Array<PgInterval, IntervalCodec>((uint)TypeOid.IntervalArray, value);
    }
    public static MpgsqlParameterValue NullableIntervalArray(ReadOnlyMemory<PgInterval?>? value)
    {
        return NullableArray<PgInterval, IntervalCodec>((uint)TypeOid.IntervalArray, value);
    }
    public static MpgsqlParameterValue Inet(PgInet? value)
    {
        return Scalar<PgInet, InetCodec>((uint)TypeOid.Inet, value);
    }
    public static MpgsqlParameterValue InetArray(ReadOnlyMemory<PgInet>? value)
    {
        return Array<PgInet, InetCodec>((uint)TypeOid.InetArray, value);
    }
    public static MpgsqlParameterValue NullableInetArray(ReadOnlyMemory<PgInet?>? value)
    {
        return NullableArray<PgInet, InetCodec>((uint)TypeOid.InetArray, value);
    }
    public static MpgsqlParameterValue Cidr(PgInet? value)
    {
        return Scalar<PgInet, CidrCodec>((uint)TypeOid.Cidr, value);
    }
    public static MpgsqlParameterValue CidrArray(ReadOnlyMemory<PgInet>? value)
    {
        return Array<PgInet, CidrCodec>((uint)TypeOid.CidrArray, value);
    }
    public static MpgsqlParameterValue NullableCidrArray(ReadOnlyMemory<PgInet?>? value)
    {
        return NullableArray<PgInet, CidrCodec>((uint)TypeOid.CidrArray, value);
    }
    public static MpgsqlParameterValue Oid(uint? value)
    {
        return Scalar<uint, OidCodec>((uint)TypeOid.Oid, value);
    }
    public static MpgsqlParameterValue OidArray(ReadOnlyMemory<uint>? value)
    {
        return Array<uint, OidCodec>((uint)TypeOid.OidArray, value);
    }
    public static MpgsqlParameterValue NullableOidArray(ReadOnlyMemory<uint?>? value)
    {
        return NullableArray<uint, OidCodec>((uint)TypeOid.OidArray, value);
    }
    public static MpgsqlParameterValue Decimal(decimal? value)
    {
        return Scalar<decimal, DecimalCodec>((uint)TypeOid.Numeric, value);
    }
    public static MpgsqlParameterValue DecimalArray(ReadOnlyMemory<decimal>? value)
    {
        return Array<decimal, DecimalCodec>((uint)TypeOid.NumericArray, value);
    }
    public static MpgsqlParameterValue NullableDecimalArray(ReadOnlyMemory<decimal?>? value)
    {
        return NullableArray<decimal, DecimalCodec>((uint)TypeOid.NumericArray, value);
    }
    public static MpgsqlParameterValue DateOnly(DateOnly? value)
    {
        return Scalar<DateOnly, DateClrCodec>((uint)TypeOid.Date, value);
    }
    public static MpgsqlParameterValue DateOnlyArray(ReadOnlyMemory<DateOnly>? value)
    {
        return Array<DateOnly, DateClrCodec>((uint)TypeOid.DateArray, value);
    }
    public static MpgsqlParameterValue NullableDateOnlyArray(ReadOnlyMemory<DateOnly?>? value)
    {
        return NullableArray<DateOnly, DateClrCodec>((uint)TypeOid.DateArray, value);
    }
    public static MpgsqlParameterValue TimeOnly(TimeOnly? value)
    {
        return Scalar<TimeOnly, TimeClrCodec>((uint)TypeOid.Time, value);
    }
    public static MpgsqlParameterValue TimeOnlyArray(ReadOnlyMemory<TimeOnly>? value)
    {
        return Array<TimeOnly, TimeClrCodec>((uint)TypeOid.TimeArray, value);
    }
    public static MpgsqlParameterValue NullableTimeOnlyArray(ReadOnlyMemory<TimeOnly?>? value)
    {
        return NullableArray<TimeOnly, TimeClrCodec>((uint)TypeOid.TimeArray, value);
    }
    public static MpgsqlParameterValue DateTime(DateTime? value)
    {
        return Scalar<DateTime, TimestampClrCodec>((uint)TypeOid.Timestamp, value);
    }
    public static MpgsqlParameterValue DateTimeArray(ReadOnlyMemory<DateTime>? value)
    {
        return Array<DateTime, TimestampClrCodec>((uint)TypeOid.TimestampArray, value);
    }
    public static MpgsqlParameterValue NullableDateTimeArray(ReadOnlyMemory<DateTime?>? value)
    {
        return NullableArray<DateTime, TimestampClrCodec>((uint)TypeOid.TimestampArray, value);
    }
    public static MpgsqlParameterValue DateTimeOffset(DateTimeOffset? value)
    {
        return Scalar<DateTimeOffset, TimestampTzClrCodec>((uint)TypeOid.TimestampTz, value);
    }
    public static MpgsqlParameterValue DateTimeOffsetArray(ReadOnlyMemory<DateTimeOffset>? value)
    {
        return Array<DateTimeOffset, TimestampTzClrCodec>((uint)TypeOid.TimestampTzArray, value);
    }
    public static MpgsqlParameterValue NullableDateTimeOffsetArray(ReadOnlyMemory<DateTimeOffset?>? value)
    {
        return NullableArray<DateTimeOffset, TimestampTzClrCodec>((uint)TypeOid.TimestampTzArray, value);
    }
    public static MpgsqlParameterValue TimeSpan(TimeSpan? value)
    {
        return Scalar<TimeSpan, IntervalClrCodec>((uint)TypeOid.Interval, value);
    }
    public static MpgsqlParameterValue TimeSpanArray(ReadOnlyMemory<TimeSpan>? value)
    {
        return Array<TimeSpan, IntervalClrCodec>((uint)TypeOid.IntervalArray, value);
    }
    public static MpgsqlParameterValue NullableTimeSpanArray(ReadOnlyMemory<TimeSpan?>? value)
    {
        return NullableArray<TimeSpan, IntervalClrCodec>((uint)TypeOid.IntervalArray, value);
    }
    public static MpgsqlParameterValue ByteaByteArrays(ReadOnlyMemory<byte[]?>? value)
    {
        return ReferenceArray<byte[], ByteArrayCodec>((uint)TypeOid.ByteaArray, value);
    }
}