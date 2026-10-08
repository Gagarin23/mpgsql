using System.Buffers;
using Mpgsql.Converters;
using Mpgsql.Types;

namespace Mpgsql.Internal;

// An explicit OID/CLR representation table; no automatic conversion between PostgreSQL types.
internal static class FieldValueDecoder<T>
{
    internal static bool Supports(uint oid) => (TypeOid)oid switch
    {
        TypeOid.Boolean => (typeof(T) == typeof(bool) || typeof(T) == typeof(bool?)),
        TypeOid.BooleanArray => (typeof(T) == typeof(ReadOnlyMemory<bool>) || typeof(T) == typeof(ReadOnlyMemory<bool>?)) || (typeof(T) == typeof(ReadOnlyMemory<bool?>) || typeof(T) == typeof(ReadOnlyMemory<bool?>?)),
        TypeOid.Bytea => (typeof(T) == typeof(ReadOnlyMemory<byte>) || typeof(T) == typeof(ReadOnlyMemory<byte>?)) || (typeof(T) == typeof(byte[])),
        TypeOid.ByteaArray => (typeof(T) == typeof(ReadOnlyMemory<ReadOnlyMemory<byte>>) || typeof(T) == typeof(ReadOnlyMemory<ReadOnlyMemory<byte>>?)) || (typeof(T) == typeof(ReadOnlyMemory<ReadOnlyMemory<byte>?>) || typeof(T) == typeof(ReadOnlyMemory<ReadOnlyMemory<byte>?>?)) || (typeof(T) == typeof(ReadOnlyMemory<byte[]?>) || typeof(T) == typeof(ReadOnlyMemory<byte[]?>?)),
        TypeOid.Int16 => (typeof(T) == typeof(short) || typeof(T) == typeof(short?)),
        TypeOid.Int16Array => (typeof(T) == typeof(ReadOnlyMemory<short>) || typeof(T) == typeof(ReadOnlyMemory<short>?)) || (typeof(T) == typeof(ReadOnlyMemory<short?>) || typeof(T) == typeof(ReadOnlyMemory<short?>?)),
        TypeOid.Int32 => (typeof(T) == typeof(int) || typeof(T) == typeof(int?)),
        TypeOid.Int32Array => (typeof(T) == typeof(ReadOnlyMemory<int>) || typeof(T) == typeof(ReadOnlyMemory<int>?)) || (typeof(T) == typeof(ReadOnlyMemory<int?>) || typeof(T) == typeof(ReadOnlyMemory<int?>?)),
        TypeOid.Float32 => (typeof(T) == typeof(float) || typeof(T) == typeof(float?)),
        TypeOid.Float32Array => (typeof(T) == typeof(ReadOnlyMemory<float>) || typeof(T) == typeof(ReadOnlyMemory<float>?)) || (typeof(T) == typeof(ReadOnlyMemory<float?>) || typeof(T) == typeof(ReadOnlyMemory<float?>?)),
        TypeOid.Float64 => (typeof(T) == typeof(double) || typeof(T) == typeof(double?)),
        TypeOid.Float64Array => (typeof(T) == typeof(ReadOnlyMemory<double>) || typeof(T) == typeof(ReadOnlyMemory<double>?)) || (typeof(T) == typeof(ReadOnlyMemory<double?>) || typeof(T) == typeof(ReadOnlyMemory<double?>?)),
        TypeOid.Numeric => (typeof(T) == typeof(PgNumeric) || typeof(T) == typeof(PgNumeric?)) || (typeof(T) == typeof(decimal) || typeof(T) == typeof(decimal?)),
        TypeOid.NumericArray => (typeof(T) == typeof(ReadOnlyMemory<PgNumeric>) || typeof(T) == typeof(ReadOnlyMemory<PgNumeric>?)) || (typeof(T) == typeof(ReadOnlyMemory<PgNumeric?>) || typeof(T) == typeof(ReadOnlyMemory<PgNumeric?>?)) || (typeof(T) == typeof(ReadOnlyMemory<decimal>) || typeof(T) == typeof(ReadOnlyMemory<decimal>?)) || (typeof(T) == typeof(ReadOnlyMemory<decimal?>) || typeof(T) == typeof(ReadOnlyMemory<decimal?>?)),
        TypeOid.Money => (typeof(T) == typeof(long) || typeof(T) == typeof(long?)),
        TypeOid.MoneyArray => (typeof(T) == typeof(ReadOnlyMemory<long>) || typeof(T) == typeof(ReadOnlyMemory<long>?)) || (typeof(T) == typeof(ReadOnlyMemory<long?>) || typeof(T) == typeof(ReadOnlyMemory<long?>?)),
        TypeOid.Text or TypeOid.VarChar or TypeOid.BpChar or TypeOid.Name => (typeof(T) == typeof(string)),
        TypeOid.TextArray or TypeOid.VarCharArray or TypeOid.BpCharArray or TypeOid.NameArray
            => (typeof(T) == typeof(ReadOnlyMemory<string?>) || typeof(T) == typeof(ReadOnlyMemory<string?>?)),
        TypeOid.Uuid => (typeof(T) == typeof(Guid) || typeof(T) == typeof(Guid?)),
        TypeOid.UuidArray => (typeof(T) == typeof(ReadOnlyMemory<Guid>) || typeof(T) == typeof(ReadOnlyMemory<Guid>?)) || (typeof(T) == typeof(ReadOnlyMemory<Guid?>) || typeof(T) == typeof(ReadOnlyMemory<Guid?>?)),
        TypeOid.Json => (typeof(T) == typeof(string)),
        TypeOid.JsonArray => (typeof(T) == typeof(ReadOnlyMemory<string?>) || typeof(T) == typeof(ReadOnlyMemory<string?>?)),
        TypeOid.Jsonb => (typeof(T) == typeof(Memory<byte>) || typeof(T) == typeof(Memory<byte>?)),
        TypeOid.JsonbArray => (typeof(T) == typeof(ReadOnlyMemory<Memory<byte>>) || typeof(T) == typeof(ReadOnlyMemory<Memory<byte>>?)) || (typeof(T) == typeof(ReadOnlyMemory<Memory<byte>?>) || typeof(T) == typeof(ReadOnlyMemory<Memory<byte>?>?)),
        TypeOid.Xml => (typeof(T) == typeof(string)),
        TypeOid.XmlArray => (typeof(T) == typeof(ReadOnlyMemory<string?>) || typeof(T) == typeof(ReadOnlyMemory<string?>?)),
        TypeOid.Date => (typeof(T) == typeof(PgDate) || typeof(T) == typeof(PgDate?)) || (typeof(T) == typeof(DateOnly) || typeof(T) == typeof(DateOnly?)),
        TypeOid.DateArray => (typeof(T) == typeof(ReadOnlyMemory<PgDate>) || typeof(T) == typeof(ReadOnlyMemory<PgDate>?)) || (typeof(T) == typeof(ReadOnlyMemory<PgDate?>) || typeof(T) == typeof(ReadOnlyMemory<PgDate?>?)) || (typeof(T) == typeof(ReadOnlyMemory<DateOnly>) || typeof(T) == typeof(ReadOnlyMemory<DateOnly>?)) || (typeof(T) == typeof(ReadOnlyMemory<DateOnly?>) || typeof(T) == typeof(ReadOnlyMemory<DateOnly?>?)),
        TypeOid.Time => (typeof(T) == typeof(PgTime) || typeof(T) == typeof(PgTime?)) || (typeof(T) == typeof(TimeOnly) || typeof(T) == typeof(TimeOnly?)),
        TypeOid.TimeArray => (typeof(T) == typeof(ReadOnlyMemory<PgTime>) || typeof(T) == typeof(ReadOnlyMemory<PgTime>?)) || (typeof(T) == typeof(ReadOnlyMemory<PgTime?>) || typeof(T) == typeof(ReadOnlyMemory<PgTime?>?)) || (typeof(T) == typeof(ReadOnlyMemory<TimeOnly>) || typeof(T) == typeof(ReadOnlyMemory<TimeOnly>?)) || (typeof(T) == typeof(ReadOnlyMemory<TimeOnly?>) || typeof(T) == typeof(ReadOnlyMemory<TimeOnly?>?)),
        TypeOid.TimeTz => (typeof(T) == typeof(PgTimeTz) || typeof(T) == typeof(PgTimeTz?)),
        TypeOid.TimeTzArray => (typeof(T) == typeof(ReadOnlyMemory<PgTimeTz>) || typeof(T) == typeof(ReadOnlyMemory<PgTimeTz>?)) || (typeof(T) == typeof(ReadOnlyMemory<PgTimeTz?>) || typeof(T) == typeof(ReadOnlyMemory<PgTimeTz?>?)),
        TypeOid.Timestamp => (typeof(T) == typeof(PgTimestamp) || typeof(T) == typeof(PgTimestamp?)) || (typeof(T) == typeof(DateTime) || typeof(T) == typeof(DateTime?)),
        TypeOid.TimestampArray => (typeof(T) == typeof(ReadOnlyMemory<PgTimestamp>) || typeof(T) == typeof(ReadOnlyMemory<PgTimestamp>?)) || (typeof(T) == typeof(ReadOnlyMemory<PgTimestamp?>) || typeof(T) == typeof(ReadOnlyMemory<PgTimestamp?>?)) || (typeof(T) == typeof(ReadOnlyMemory<DateTime>) || typeof(T) == typeof(ReadOnlyMemory<DateTime>?)) || (typeof(T) == typeof(ReadOnlyMemory<DateTime?>) || typeof(T) == typeof(ReadOnlyMemory<DateTime?>?)),
        TypeOid.TimestampTz => (typeof(T) == typeof(PgTimestampTz) || typeof(T) == typeof(PgTimestampTz?)) || (typeof(T) == typeof(DateTimeOffset) || typeof(T) == typeof(DateTimeOffset?)),
        TypeOid.TimestampTzArray => (typeof(T) == typeof(ReadOnlyMemory<PgTimestampTz>) || typeof(T) == typeof(ReadOnlyMemory<PgTimestampTz>?)) || (typeof(T) == typeof(ReadOnlyMemory<PgTimestampTz?>) || typeof(T) == typeof(ReadOnlyMemory<PgTimestampTz?>?)) || (typeof(T) == typeof(ReadOnlyMemory<DateTimeOffset>) || typeof(T) == typeof(ReadOnlyMemory<DateTimeOffset>?)) || (typeof(T) == typeof(ReadOnlyMemory<DateTimeOffset?>) || typeof(T) == typeof(ReadOnlyMemory<DateTimeOffset?>?)),
        TypeOid.Interval => (typeof(T) == typeof(PgInterval) || typeof(T) == typeof(PgInterval?)) || (typeof(T) == typeof(TimeSpan) || typeof(T) == typeof(TimeSpan?)),
        TypeOid.IntervalArray => (typeof(T) == typeof(ReadOnlyMemory<PgInterval>) || typeof(T) == typeof(ReadOnlyMemory<PgInterval>?)) || (typeof(T) == typeof(ReadOnlyMemory<PgInterval?>) || typeof(T) == typeof(ReadOnlyMemory<PgInterval?>?)) || (typeof(T) == typeof(ReadOnlyMemory<TimeSpan>) || typeof(T) == typeof(ReadOnlyMemory<TimeSpan>?)) || (typeof(T) == typeof(ReadOnlyMemory<TimeSpan?>) || typeof(T) == typeof(ReadOnlyMemory<TimeSpan?>?)),
        TypeOid.Inet => (typeof(T) == typeof(PgInet) || typeof(T) == typeof(PgInet?)),
        TypeOid.InetArray => (typeof(T) == typeof(ReadOnlyMemory<PgInet>) || typeof(T) == typeof(ReadOnlyMemory<PgInet>?)) || (typeof(T) == typeof(ReadOnlyMemory<PgInet?>) || typeof(T) == typeof(ReadOnlyMemory<PgInet?>?)),
        TypeOid.Cidr => (typeof(T) == typeof(PgInet) || typeof(T) == typeof(PgInet?)),
        TypeOid.CidrArray => (typeof(T) == typeof(ReadOnlyMemory<PgInet>) || typeof(T) == typeof(ReadOnlyMemory<PgInet>?)) || (typeof(T) == typeof(ReadOnlyMemory<PgInet?>) || typeof(T) == typeof(ReadOnlyMemory<PgInet?>?)),
        TypeOid.Oid => (typeof(T) == typeof(uint) || typeof(T) == typeof(uint?)),
        TypeOid.OidArray => (typeof(T) == typeof(ReadOnlyMemory<uint>) || typeof(T) == typeof(ReadOnlyMemory<uint>?)) || (typeof(T) == typeof(ReadOnlyMemory<uint?>) || typeof(T) == typeof(ReadOnlyMemory<uint?>?)),
        TypeOid.Int64 => (typeof(T) == typeof(long) || typeof(T) == typeof(long?)),
        TypeOid.Int64Array => (typeof(T) == typeof(ReadOnlyMemory<long>) || typeof(T) == typeof(ReadOnlyMemory<long>?)) || (typeof(T) == typeof(ReadOnlyMemory<long?>) || typeof(T) == typeof(ReadOnlyMemory<long?>?)),
        _ => false
    };

    internal static T Read(uint oid, ReadOnlySequence<byte> payload)
    {
        switch ((TypeOid)oid)
        {
            case TypeOid.Boolean:
                if (typeof(T) == typeof(bool) || typeof(T) == typeof(bool?)) return (T)(object)BooleanCodec.Read(payload);
                break;
            case TypeOid.BooleanArray:
                if (typeof(T) == typeof(ReadOnlyMemory<bool>) || typeof(T) == typeof(ReadOnlyMemory<bool>?)) return (T)(object)BinaryArray<bool, BooleanCodec>.Read(payload);
                if (typeof(T) == typeof(ReadOnlyMemory<bool?>) || typeof(T) == typeof(ReadOnlyMemory<bool?>?)) return (T)(object)BinaryNullableArray<bool, BooleanCodec>.Read(payload);
                break;
            case TypeOid.Bytea:
                if (typeof(T) == typeof(ReadOnlyMemory<byte>) || typeof(T) == typeof(ReadOnlyMemory<byte>?)) return (T)(object)ByteaCodec.Read(payload);
                if (typeof(T) == typeof(byte[])) return (T)(object)ByteArrayCodec.Read(payload);
                break;
            case TypeOid.ByteaArray:
                if (typeof(T) == typeof(ReadOnlyMemory<ReadOnlyMemory<byte>>) || typeof(T) == typeof(ReadOnlyMemory<ReadOnlyMemory<byte>>?)) return (T)(object)BinaryArray<ReadOnlyMemory<byte>, ByteaCodec>.Read(payload);
                if (typeof(T) == typeof(ReadOnlyMemory<ReadOnlyMemory<byte>?>) || typeof(T) == typeof(ReadOnlyMemory<ReadOnlyMemory<byte>?>?)) return (T)(object)BinaryNullableArray<ReadOnlyMemory<byte>, ByteaCodec>.Read(payload);
                if (typeof(T) == typeof(ReadOnlyMemory<byte[]?>) || typeof(T) == typeof(ReadOnlyMemory<byte[]?>?)) return (T)(object)BinaryReferenceArray<byte[], ByteArrayCodec>.Read(payload);
                break;
            case TypeOid.Int16:
                if (typeof(T) == typeof(short) || typeof(T) == typeof(short?)) return (T)(object)Int16Codec.Read(payload);
                break;
            case TypeOid.Int16Array:
                if (typeof(T) == typeof(ReadOnlyMemory<short>) || typeof(T) == typeof(ReadOnlyMemory<short>?)) return (T)(object)BinaryArray<short, Int16Codec>.Read(payload);
                if (typeof(T) == typeof(ReadOnlyMemory<short?>) || typeof(T) == typeof(ReadOnlyMemory<short?>?)) return (T)(object)BinaryNullableArray<short, Int16Codec>.Read(payload);
                break;
            case TypeOid.Int32:
                if (typeof(T) == typeof(int) || typeof(T) == typeof(int?)) return (T)(object)Int32Codec.Read(payload);
                break;
            case TypeOid.Int32Array:
                if (typeof(T) == typeof(ReadOnlyMemory<int>) || typeof(T) == typeof(ReadOnlyMemory<int>?)) return (T)(object)BinaryArray<int, Int32Codec>.Read(payload);
                if (typeof(T) == typeof(ReadOnlyMemory<int?>) || typeof(T) == typeof(ReadOnlyMemory<int?>?)) return (T)(object)BinaryNullableArray<int, Int32Codec>.Read(payload);
                break;
            case TypeOid.Float32:
                if (typeof(T) == typeof(float) || typeof(T) == typeof(float?)) return (T)(object)Float32Codec.Read(payload);
                break;
            case TypeOid.Float32Array:
                if (typeof(T) == typeof(ReadOnlyMemory<float>) || typeof(T) == typeof(ReadOnlyMemory<float>?)) return (T)(object)BinaryArray<float, Float32Codec>.Read(payload);
                if (typeof(T) == typeof(ReadOnlyMemory<float?>) || typeof(T) == typeof(ReadOnlyMemory<float?>?)) return (T)(object)BinaryNullableArray<float, Float32Codec>.Read(payload);
                break;
            case TypeOid.Float64:
                if (typeof(T) == typeof(double) || typeof(T) == typeof(double?)) return (T)(object)Float64Codec.Read(payload);
                break;
            case TypeOid.Float64Array:
                if (typeof(T) == typeof(ReadOnlyMemory<double>) || typeof(T) == typeof(ReadOnlyMemory<double>?)) return (T)(object)BinaryArray<double, Float64Codec>.Read(payload);
                if (typeof(T) == typeof(ReadOnlyMemory<double?>) || typeof(T) == typeof(ReadOnlyMemory<double?>?)) return (T)(object)BinaryNullableArray<double, Float64Codec>.Read(payload);
                break;
            case TypeOid.Numeric:
                if (typeof(T) == typeof(PgNumeric) || typeof(T) == typeof(PgNumeric?)) return (T)(object)NumericCodec.Read(payload);
                if (typeof(T) == typeof(decimal) || typeof(T) == typeof(decimal?)) return (T)(object)DecimalCodec.Read(payload);
                break;
            case TypeOid.NumericArray:
                if (typeof(T) == typeof(ReadOnlyMemory<PgNumeric>) || typeof(T) == typeof(ReadOnlyMemory<PgNumeric>?)) return (T)(object)BinaryArray<PgNumeric, NumericCodec>.Read(payload);
                if (typeof(T) == typeof(ReadOnlyMemory<PgNumeric?>) || typeof(T) == typeof(ReadOnlyMemory<PgNumeric?>?)) return (T)(object)BinaryNullableArray<PgNumeric, NumericCodec>.Read(payload);
                if (typeof(T) == typeof(ReadOnlyMemory<decimal>) || typeof(T) == typeof(ReadOnlyMemory<decimal>?)) return (T)(object)BinaryArray<decimal, DecimalCodec>.Read(payload);
                if (typeof(T) == typeof(ReadOnlyMemory<decimal?>) || typeof(T) == typeof(ReadOnlyMemory<decimal?>?)) return (T)(object)BinaryNullableArray<decimal, DecimalCodec>.Read(payload);
                break;
            case TypeOid.Money:
                if (typeof(T) == typeof(long) || typeof(T) == typeof(long?)) return (T)(object)MoneyCodec.Read(payload);
                break;
            case TypeOid.MoneyArray:
                if (typeof(T) == typeof(ReadOnlyMemory<long>) || typeof(T) == typeof(ReadOnlyMemory<long>?)) return (T)(object)BinaryArray<long, MoneyCodec>.Read(payload);
                if (typeof(T) == typeof(ReadOnlyMemory<long?>) || typeof(T) == typeof(ReadOnlyMemory<long?>?)) return (T)(object)BinaryNullableArray<long, MoneyCodec>.Read(payload);
                break;
            case TypeOid.Text:
            case TypeOid.VarChar:
            case TypeOid.BpChar:
            case TypeOid.Name:
                // Same UTF-8 payload format as text; bpchar's received trailing spaces are retained.
                if (typeof(T) == typeof(string)) return (T)(object)TextCodec.Read(payload);
                break;
            case TypeOid.TextArray:
                if (typeof(T) == typeof(ReadOnlyMemory<string?>) || typeof(T) == typeof(ReadOnlyMemory<string?>?)) return (T)(object)BinaryReferenceArray<string, TextCodec>.Read(payload);
                break;
            case TypeOid.VarCharArray:
                if (typeof(T) == typeof(ReadOnlyMemory<string?>) || typeof(T) == typeof(ReadOnlyMemory<string?>?)) return (T)(object)BinaryReferenceArray<string, VarCharCodec>.Read(payload);
                break;
            case TypeOid.BpCharArray:
                if (typeof(T) == typeof(ReadOnlyMemory<string?>) || typeof(T) == typeof(ReadOnlyMemory<string?>?)) return (T)(object)BinaryReferenceArray<string, BpCharCodec>.Read(payload);
                break;
            case TypeOid.NameArray:
                if (typeof(T) == typeof(ReadOnlyMemory<string?>) || typeof(T) == typeof(ReadOnlyMemory<string?>?)) return (T)(object)BinaryReferenceArray<string, NameCodec>.Read(payload);
                break;
            case TypeOid.Uuid:
                if (typeof(T) == typeof(Guid) || typeof(T) == typeof(Guid?)) return (T)(object)UuidCodec.Read(payload);
                break;
            case TypeOid.UuidArray:
                if (typeof(T) == typeof(ReadOnlyMemory<Guid>) || typeof(T) == typeof(ReadOnlyMemory<Guid>?)) return (T)(object)BinaryArray<Guid, UuidCodec>.Read(payload);
                if (typeof(T) == typeof(ReadOnlyMemory<Guid?>) || typeof(T) == typeof(ReadOnlyMemory<Guid?>?)) return (T)(object)BinaryNullableArray<Guid, UuidCodec>.Read(payload);
                break;
            case TypeOid.Json:
                if (typeof(T) == typeof(string)) return (T)(object)JsonCodec.Read(payload);
                break;
            case TypeOid.JsonArray:
                if (typeof(T) == typeof(ReadOnlyMemory<string?>) || typeof(T) == typeof(ReadOnlyMemory<string?>?)) return (T)(object)BinaryReferenceArray<string, JsonCodec>.Read(payload);
                break;
            case TypeOid.Jsonb:
                if (typeof(T) == typeof(Memory<byte>) || typeof(T) == typeof(Memory<byte>?)) return (T)(object)JsonbCodec.Read(payload);
                break;
            case TypeOid.JsonbArray:
                if (typeof(T) == typeof(ReadOnlyMemory<Memory<byte>>) || typeof(T) == typeof(ReadOnlyMemory<Memory<byte>>?)) return (T)(object)BinaryArray<Memory<byte>, JsonbCodec>.Read(payload);
                if (typeof(T) == typeof(ReadOnlyMemory<Memory<byte>?>) || typeof(T) == typeof(ReadOnlyMemory<Memory<byte>?>?)) return (T)(object)BinaryNullableArray<Memory<byte>, JsonbCodec>.Read(payload);
                break;
            case TypeOid.Xml:
                if (typeof(T) == typeof(string)) return (T)(object)XmlCodec.Read(payload);
                break;
            case TypeOid.XmlArray:
                if (typeof(T) == typeof(ReadOnlyMemory<string?>) || typeof(T) == typeof(ReadOnlyMemory<string?>?)) return (T)(object)BinaryReferenceArray<string, XmlCodec>.Read(payload);
                break;
            case TypeOid.Date:
                if (typeof(T) == typeof(PgDate) || typeof(T) == typeof(PgDate?)) return (T)(object)DateCodec.Read(payload);
                if (typeof(T) == typeof(DateOnly) || typeof(T) == typeof(DateOnly?)) return (T)(object)DateClrCodec.Read(payload);
                break;
            case TypeOid.DateArray:
                if (typeof(T) == typeof(ReadOnlyMemory<PgDate>) || typeof(T) == typeof(ReadOnlyMemory<PgDate>?)) return (T)(object)BinaryArray<PgDate, DateCodec>.Read(payload);
                if (typeof(T) == typeof(ReadOnlyMemory<PgDate?>) || typeof(T) == typeof(ReadOnlyMemory<PgDate?>?)) return (T)(object)BinaryNullableArray<PgDate, DateCodec>.Read(payload);
                if (typeof(T) == typeof(ReadOnlyMemory<DateOnly>) || typeof(T) == typeof(ReadOnlyMemory<DateOnly>?)) return (T)(object)BinaryArray<DateOnly, DateClrCodec>.Read(payload);
                if (typeof(T) == typeof(ReadOnlyMemory<DateOnly?>) || typeof(T) == typeof(ReadOnlyMemory<DateOnly?>?)) return (T)(object)BinaryNullableArray<DateOnly, DateClrCodec>.Read(payload);
                break;
            case TypeOid.Time:
                if (typeof(T) == typeof(PgTime) || typeof(T) == typeof(PgTime?)) return (T)(object)TimeCodec.Read(payload);
                if (typeof(T) == typeof(TimeOnly) || typeof(T) == typeof(TimeOnly?)) return (T)(object)TimeClrCodec.Read(payload);
                break;
            case TypeOid.TimeArray:
                if (typeof(T) == typeof(ReadOnlyMemory<PgTime>) || typeof(T) == typeof(ReadOnlyMemory<PgTime>?)) return (T)(object)BinaryArray<PgTime, TimeCodec>.Read(payload);
                if (typeof(T) == typeof(ReadOnlyMemory<PgTime?>) || typeof(T) == typeof(ReadOnlyMemory<PgTime?>?)) return (T)(object)BinaryNullableArray<PgTime, TimeCodec>.Read(payload);
                if (typeof(T) == typeof(ReadOnlyMemory<TimeOnly>) || typeof(T) == typeof(ReadOnlyMemory<TimeOnly>?)) return (T)(object)BinaryArray<TimeOnly, TimeClrCodec>.Read(payload);
                if (typeof(T) == typeof(ReadOnlyMemory<TimeOnly?>) || typeof(T) == typeof(ReadOnlyMemory<TimeOnly?>?)) return (T)(object)BinaryNullableArray<TimeOnly, TimeClrCodec>.Read(payload);
                break;
            case TypeOid.TimeTz:
                if (typeof(T) == typeof(PgTimeTz) || typeof(T) == typeof(PgTimeTz?)) return (T)(object)TimeTzCodec.Read(payload);
                break;
            case TypeOid.TimeTzArray:
                if (typeof(T) == typeof(ReadOnlyMemory<PgTimeTz>) || typeof(T) == typeof(ReadOnlyMemory<PgTimeTz>?)) return (T)(object)BinaryArray<PgTimeTz, TimeTzCodec>.Read(payload);
                if (typeof(T) == typeof(ReadOnlyMemory<PgTimeTz?>) || typeof(T) == typeof(ReadOnlyMemory<PgTimeTz?>?)) return (T)(object)BinaryNullableArray<PgTimeTz, TimeTzCodec>.Read(payload);
                break;
            case TypeOid.Timestamp:
                if (typeof(T) == typeof(PgTimestamp) || typeof(T) == typeof(PgTimestamp?)) return (T)(object)TimestampCodec.Read(payload);
                if (typeof(T) == typeof(DateTime) || typeof(T) == typeof(DateTime?)) return (T)(object)TimestampClrCodec.Read(payload);
                break;
            case TypeOid.TimestampArray:
                if (typeof(T) == typeof(ReadOnlyMemory<PgTimestamp>) || typeof(T) == typeof(ReadOnlyMemory<PgTimestamp>?)) return (T)(object)BinaryArray<PgTimestamp, TimestampCodec>.Read(payload);
                if (typeof(T) == typeof(ReadOnlyMemory<PgTimestamp?>) || typeof(T) == typeof(ReadOnlyMemory<PgTimestamp?>?)) return (T)(object)BinaryNullableArray<PgTimestamp, TimestampCodec>.Read(payload);
                if (typeof(T) == typeof(ReadOnlyMemory<DateTime>) || typeof(T) == typeof(ReadOnlyMemory<DateTime>?)) return (T)(object)BinaryArray<DateTime, TimestampClrCodec>.Read(payload);
                if (typeof(T) == typeof(ReadOnlyMemory<DateTime?>) || typeof(T) == typeof(ReadOnlyMemory<DateTime?>?)) return (T)(object)BinaryNullableArray<DateTime, TimestampClrCodec>.Read(payload);
                break;
            case TypeOid.TimestampTz:
                if (typeof(T) == typeof(PgTimestampTz) || typeof(T) == typeof(PgTimestampTz?)) return (T)(object)TimestampTzCodec.Read(payload);
                if (typeof(T) == typeof(DateTimeOffset) || typeof(T) == typeof(DateTimeOffset?)) return (T)(object)TimestampTzClrCodec.Read(payload);
                break;
            case TypeOid.TimestampTzArray:
                if (typeof(T) == typeof(ReadOnlyMemory<PgTimestampTz>) || typeof(T) == typeof(ReadOnlyMemory<PgTimestampTz>?)) return (T)(object)BinaryArray<PgTimestampTz, TimestampTzCodec>.Read(payload);
                if (typeof(T) == typeof(ReadOnlyMemory<PgTimestampTz?>) || typeof(T) == typeof(ReadOnlyMemory<PgTimestampTz?>?)) return (T)(object)BinaryNullableArray<PgTimestampTz, TimestampTzCodec>.Read(payload);
                if (typeof(T) == typeof(ReadOnlyMemory<DateTimeOffset>) || typeof(T) == typeof(ReadOnlyMemory<DateTimeOffset>?)) return (T)(object)BinaryArray<DateTimeOffset, TimestampTzClrCodec>.Read(payload);
                if (typeof(T) == typeof(ReadOnlyMemory<DateTimeOffset?>) || typeof(T) == typeof(ReadOnlyMemory<DateTimeOffset?>?)) return (T)(object)BinaryNullableArray<DateTimeOffset, TimestampTzClrCodec>.Read(payload);
                break;
            case TypeOid.Interval:
                if (typeof(T) == typeof(PgInterval) || typeof(T) == typeof(PgInterval?)) return (T)(object)IntervalCodec.Read(payload);
                if (typeof(T) == typeof(TimeSpan) || typeof(T) == typeof(TimeSpan?)) return (T)(object)IntervalClrCodec.Read(payload);
                break;
            case TypeOid.IntervalArray:
                if (typeof(T) == typeof(ReadOnlyMemory<PgInterval>) || typeof(T) == typeof(ReadOnlyMemory<PgInterval>?)) return (T)(object)BinaryArray<PgInterval, IntervalCodec>.Read(payload);
                if (typeof(T) == typeof(ReadOnlyMemory<PgInterval?>) || typeof(T) == typeof(ReadOnlyMemory<PgInterval?>?)) return (T)(object)BinaryNullableArray<PgInterval, IntervalCodec>.Read(payload);
                if (typeof(T) == typeof(ReadOnlyMemory<TimeSpan>) || typeof(T) == typeof(ReadOnlyMemory<TimeSpan>?)) return (T)(object)BinaryArray<TimeSpan, IntervalClrCodec>.Read(payload);
                if (typeof(T) == typeof(ReadOnlyMemory<TimeSpan?>) || typeof(T) == typeof(ReadOnlyMemory<TimeSpan?>?)) return (T)(object)BinaryNullableArray<TimeSpan, IntervalClrCodec>.Read(payload);
                break;
            case TypeOid.Inet:
                if (typeof(T) == typeof(PgInet) || typeof(T) == typeof(PgInet?)) return (T)(object)InetCodec.Read(payload);
                break;
            case TypeOid.InetArray:
                if (typeof(T) == typeof(ReadOnlyMemory<PgInet>) || typeof(T) == typeof(ReadOnlyMemory<PgInet>?)) return (T)(object)BinaryArray<PgInet, InetCodec>.Read(payload);
                if (typeof(T) == typeof(ReadOnlyMemory<PgInet?>) || typeof(T) == typeof(ReadOnlyMemory<PgInet?>?)) return (T)(object)BinaryNullableArray<PgInet, InetCodec>.Read(payload);
                break;
            case TypeOid.Cidr:
                if (typeof(T) == typeof(PgInet) || typeof(T) == typeof(PgInet?)) return (T)(object)CidrCodec.Read(payload);
                break;
            case TypeOid.CidrArray:
                if (typeof(T) == typeof(ReadOnlyMemory<PgInet>) || typeof(T) == typeof(ReadOnlyMemory<PgInet>?)) return (T)(object)BinaryArray<PgInet, CidrCodec>.Read(payload);
                if (typeof(T) == typeof(ReadOnlyMemory<PgInet?>) || typeof(T) == typeof(ReadOnlyMemory<PgInet?>?)) return (T)(object)BinaryNullableArray<PgInet, CidrCodec>.Read(payload);
                break;
            case TypeOid.Oid:
                if (typeof(T) == typeof(uint) || typeof(T) == typeof(uint?)) return (T)(object)OidCodec.Read(payload);
                break;
            case TypeOid.OidArray:
                if (typeof(T) == typeof(ReadOnlyMemory<uint>) || typeof(T) == typeof(ReadOnlyMemory<uint>?)) return (T)(object)BinaryArray<uint, OidCodec>.Read(payload);
                if (typeof(T) == typeof(ReadOnlyMemory<uint?>) || typeof(T) == typeof(ReadOnlyMemory<uint?>?)) return (T)(object)BinaryNullableArray<uint, OidCodec>.Read(payload);
                break;
            case TypeOid.Int64:
                if (typeof(T) == typeof(long) || typeof(T) == typeof(long?)) return (T)(object)Int64Converter.Read(payload);
                break;
            case TypeOid.Int64Array:
                if (typeof(T) == typeof(ReadOnlyMemory<long>) || typeof(T) == typeof(ReadOnlyMemory<long>?)) return (T)(object)Int64ArrayConverter.Read(payload);
                if (typeof(T) == typeof(ReadOnlyMemory<long?>) || typeof(T) == typeof(ReadOnlyMemory<long?>?)) return (T)(object)NullableInt64ArrayConverter.Read(payload);
                break;
        }
        throw new InvalidCastException($"PostgreSQL type {oid} cannot be read as {typeof(T)}.");
    }
}
