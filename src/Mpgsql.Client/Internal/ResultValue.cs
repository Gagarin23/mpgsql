using System.Globalization;
using Mpgsql.Types;

namespace Mpgsql.Internal;

internal static class ResultValue
{
    internal static string TypeName(uint oid)
    {
        return (TypeOid)oid switch
        {
            TypeOid.Boolean     => "bool", TypeOid.BooleanArray            => "bool[]", TypeOid.Bytea           => "bytea", TypeOid.ByteaArray         => "bytea[]",
            TypeOid.Int16       => "int2", TypeOid.Int16Array              => "int2[]", TypeOid.Int32           => "int4", TypeOid.Int32Array          => "int4[]",
            TypeOid.Int64       => "int8", TypeOid.Int64Array              => "int8[]", TypeOid.Float32         => "float4", TypeOid.Float32Array      => "float4[]",
            TypeOid.Float64     => "float8", TypeOid.Float64Array          => "float8[]", TypeOid.Numeric       => "numeric", TypeOid.NumericArray     => "numeric[]",
            TypeOid.Money       => "money", TypeOid.MoneyArray             => "money[]", TypeOid.Oid            => "oid", TypeOid.OidArray             => "oid[]",
            TypeOid.Text        => "text", TypeOid.TextArray               => "text[]", TypeOid.VarChar         => "varchar", TypeOid.VarCharArray     => "varchar[]",
            TypeOid.BpChar      => "bpchar", TypeOid.BpCharArray           => "bpchar[]", TypeOid.Name          => "name", TypeOid.NameArray           => "name[]",
            TypeOid.Uuid        => "uuid", TypeOid.UuidArray               => "uuid[]", TypeOid.Json            => "json", TypeOid.JsonArray           => "json[]",
            TypeOid.Jsonb       => "jsonb", TypeOid.JsonbArray             => "jsonb[]", TypeOid.Xml            => "xml", TypeOid.XmlArray             => "xml[]",
            TypeOid.Date        => "date", TypeOid.DateArray               => "date[]", TypeOid.Time            => "time", TypeOid.TimeArray           => "time[]",
            TypeOid.TimeTz      => "timetz", TypeOid.TimeTzArray           => "timetz[]", TypeOid.Timestamp     => "timestamp", TypeOid.TimestampArray => "timestamp[]",
            TypeOid.TimestampTz => "timestamptz", TypeOid.TimestampTzArray => "timestamptz[]", TypeOid.Interval => "interval", TypeOid.IntervalArray   => "interval[]",
            TypeOid.Inet        => "inet", TypeOid.InetArray               => "inet[]", TypeOid.Cidr            => "cidr", TypeOid.CidrArray           => "cidr[]",
            _                   => oid.ToString(CultureInfo.InvariantCulture)
        };
    }
    internal static Type ClrType(uint oid)
    {
        return (TypeOid)oid switch
        {
            TypeOid.Boolean => typeof(bool), TypeOid.Int16 => typeof(short), TypeOid.Int32 => typeof(int),
            TypeOid.Int64 or TypeOid.Money => typeof(long), TypeOid.Oid => typeof(uint),
            TypeOid.Float32 => typeof(float), TypeOid.Float64 => typeof(double), TypeOid.Numeric => typeof(PgNumeric),
            TypeOid.Uuid => typeof(Guid), TypeOid.Bytea => typeof(ReadOnlyMemory<byte>), TypeOid.Jsonb => typeof(Memory<byte>),
            TypeOid.Text or TypeOid.VarChar or TypeOid.BpChar or TypeOid.Name or TypeOid.Json or TypeOid.Xml => typeof(string),
            TypeOid.Date => typeof(PgDate), TypeOid.Time => typeof(PgTime), TypeOid.TimeTz => typeof(PgTimeTz),
            TypeOid.Timestamp => typeof(PgTimestamp), TypeOid.TimestampTz => typeof(PgTimestampTz),
            TypeOid.Interval => typeof(PgInterval), TypeOid.Inet or TypeOid.Cidr => typeof(PgInet),
            TypeOid.BooleanArray => typeof(ReadOnlyMemory<bool?>), TypeOid.Int16Array => typeof(ReadOnlyMemory<short?>),
            TypeOid.Int32Array => typeof(ReadOnlyMemory<int?>), TypeOid.Int64Array or TypeOid.MoneyArray => typeof(ReadOnlyMemory<long?>),
            TypeOid.OidArray => typeof(ReadOnlyMemory<uint?>), TypeOid.Float32Array => typeof(ReadOnlyMemory<float?>),
            TypeOid.Float64Array => typeof(ReadOnlyMemory<double?>), TypeOid.NumericArray => typeof(ReadOnlyMemory<PgNumeric?>),
            TypeOid.UuidArray => typeof(ReadOnlyMemory<Guid?>), TypeOid.ByteaArray => typeof(ReadOnlyMemory<ReadOnlyMemory<byte>?>),
            TypeOid.JsonbArray => typeof(ReadOnlyMemory<Memory<byte>?>),
            TypeOid.TextArray or TypeOid.VarCharArray or TypeOid.BpCharArray or TypeOid.NameArray or TypeOid.JsonArray or TypeOid.XmlArray => typeof(ReadOnlyMemory<string?>),
            TypeOid.DateArray => typeof(ReadOnlyMemory<PgDate?>), TypeOid.TimeArray => typeof(ReadOnlyMemory<PgTime?>),
            TypeOid.TimeTzArray => typeof(ReadOnlyMemory<PgTimeTz?>), TypeOid.TimestampArray => typeof(ReadOnlyMemory<PgTimestamp?>),
            TypeOid.TimestampTzArray => typeof(ReadOnlyMemory<PgTimestampTz?>), TypeOid.IntervalArray => typeof(ReadOnlyMemory<PgInterval?>),
            TypeOid.InetArray or TypeOid.CidrArray => typeof(ReadOnlyMemory<PgInet?>),
            _ => throw new NotSupportedException($"PostgreSQL OID {oid} has no object representation; use GetRawValue.")
        };
    }

    internal static object Read(MpgsqlResultReader reader, int ordinal)
    {
        return (TypeOid)reader.Columns.Span[ordinal].DataTypeOid switch
        {
            TypeOid.Boolean => reader.GetFieldValue<bool>(ordinal), TypeOid.Int16 => reader.GetFieldValue<short>(ordinal),
            TypeOid.Int32 => reader.GetFieldValue<int>(ordinal), TypeOid.Int64 or TypeOid.Money => reader.GetFieldValue<long>(ordinal),
            TypeOid.Oid => reader.GetFieldValue<uint>(ordinal), TypeOid.Float32 => reader.GetFieldValue<float>(ordinal),
            TypeOid.Float64 => reader.GetFieldValue<double>(ordinal), TypeOid.Numeric => reader.GetFieldValue<PgNumeric>(ordinal),
            TypeOid.Uuid => reader.GetFieldValue<Guid>(ordinal), TypeOid.Bytea => reader.GetFieldValue<ReadOnlyMemory<byte>>(ordinal),
            TypeOid.Jsonb => reader.GetFieldValue<Memory<byte>>(ordinal),
            TypeOid.Text or TypeOid.VarChar or TypeOid.BpChar or TypeOid.Name or TypeOid.Json or TypeOid.Xml => reader.GetFieldValue<string>(ordinal),
            TypeOid.Date => reader.GetFieldValue<PgDate>(ordinal), TypeOid.Time => reader.GetFieldValue<PgTime>(ordinal),
            TypeOid.TimeTz => reader.GetFieldValue<PgTimeTz>(ordinal), TypeOid.Timestamp => reader.GetFieldValue<PgTimestamp>(ordinal),
            TypeOid.TimestampTz => reader.GetFieldValue<PgTimestampTz>(ordinal), TypeOid.Interval => reader.GetFieldValue<PgInterval>(ordinal),
            TypeOid.Inet or TypeOid.Cidr => reader.GetFieldValue<PgInet>(ordinal),
            TypeOid.BooleanArray => reader.GetFieldValue<ReadOnlyMemory<bool?>>(ordinal), TypeOid.Int16Array => reader.GetFieldValue<ReadOnlyMemory<short?>>(ordinal),
            TypeOid.Int32Array => reader.GetFieldValue<ReadOnlyMemory<int?>>(ordinal), TypeOid.Int64Array or TypeOid.MoneyArray => reader.GetFieldValue<ReadOnlyMemory<long?>>(ordinal),
            TypeOid.OidArray => reader.GetFieldValue<ReadOnlyMemory<uint?>>(ordinal), TypeOid.Float32Array => reader.GetFieldValue<ReadOnlyMemory<float?>>(ordinal),
            TypeOid.Float64Array => reader.GetFieldValue<ReadOnlyMemory<double?>>(ordinal), TypeOid.NumericArray => reader.GetFieldValue<ReadOnlyMemory<PgNumeric?>>(ordinal),
            TypeOid.UuidArray => reader.GetFieldValue<ReadOnlyMemory<Guid?>>(ordinal), TypeOid.ByteaArray => reader.GetFieldValue<ReadOnlyMemory<ReadOnlyMemory<byte>?>>(ordinal),
            TypeOid.JsonbArray => reader.GetFieldValue<ReadOnlyMemory<Memory<byte>?>>(ordinal),
            TypeOid.TextArray or TypeOid.VarCharArray or TypeOid.BpCharArray or TypeOid.NameArray or TypeOid.JsonArray or TypeOid.XmlArray => reader.GetFieldValue<ReadOnlyMemory<string?>>(ordinal),
            TypeOid.DateArray => reader.GetFieldValue<ReadOnlyMemory<PgDate?>>(ordinal), TypeOid.TimeArray => reader.GetFieldValue<ReadOnlyMemory<PgTime?>>(ordinal),
            TypeOid.TimeTzArray => reader.GetFieldValue<ReadOnlyMemory<PgTimeTz?>>(ordinal), TypeOid.TimestampArray => reader.GetFieldValue<ReadOnlyMemory<PgTimestamp?>>(ordinal),
            TypeOid.TimestampTzArray => reader.GetFieldValue<ReadOnlyMemory<PgTimestampTz?>>(ordinal), TypeOid.IntervalArray => reader.GetFieldValue<ReadOnlyMemory<PgInterval?>>(ordinal),
            TypeOid.InetArray or TypeOid.CidrArray => reader.GetFieldValue<ReadOnlyMemory<PgInet?>>(ordinal),
            _ => throw new NotSupportedException("Use GetRawValue for this PostgreSQL type.")
        };
    }
}