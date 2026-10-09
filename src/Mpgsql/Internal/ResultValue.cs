using System.Buffers;
using System.Globalization;
using Mpgsql.Types;

namespace Mpgsql.Internal;

internal static class ResultValue
{
    internal static string TypeName(uint oid)
    {
        return (TypeOid)oid switch
        {
            TypeOid.Boolean          => "bool",
            TypeOid.BooleanArray     => "bool[]",
            TypeOid.Bytea            => "bytea",
            TypeOid.ByteaArray       => "bytea[]",
            TypeOid.Int16            => "int2",
            TypeOid.Int16Array       => "int2[]",
            TypeOid.Int32            => "int4",
            TypeOid.Int32Array       => "int4[]",
            TypeOid.Int64            => "int8",
            TypeOid.Int64Array       => "int8[]",
            TypeOid.Float32          => "float4",
            TypeOid.Float32Array     => "float4[]",
            TypeOid.Float64          => "float8",
            TypeOid.Float64Array     => "float8[]",
            TypeOid.Numeric          => "numeric",
            TypeOid.NumericArray     => "numeric[]",
            TypeOid.Money            => "money",
            TypeOid.MoneyArray       => "money[]",
            TypeOid.Oid              => "oid",
            TypeOid.OidArray         => "oid[]",
            TypeOid.Text             => "text",
            TypeOid.TextArray        => "text[]",
            TypeOid.VarChar          => "varchar",
            TypeOid.VarCharArray     => "varchar[]",
            TypeOid.BpChar           => "bpchar",
            TypeOid.BpCharArray      => "bpchar[]",
            TypeOid.Name             => "name",
            TypeOid.NameArray        => "name[]",
            TypeOid.Uuid             => "uuid",
            TypeOid.UuidArray        => "uuid[]",
            TypeOid.Json             => "json",
            TypeOid.JsonArray        => "json[]",
            TypeOid.Jsonb            => "jsonb",
            TypeOid.JsonbArray       => "jsonb[]",
            TypeOid.Xml              => "xml",
            TypeOid.XmlArray         => "xml[]",
            TypeOid.Date             => "date",
            TypeOid.DateArray        => "date[]",
            TypeOid.Time             => "time",
            TypeOid.TimeArray        => "time[]",
            TypeOid.TimeTz           => "timetz",
            TypeOid.TimeTzArray      => "timetz[]",
            TypeOid.Timestamp        => "timestamp",
            TypeOid.TimestampArray   => "timestamp[]",
            TypeOid.TimestampTz      => "timestamptz",
            TypeOid.TimestampTzArray => "timestamptz[]",
            TypeOid.Interval         => "interval",
            TypeOid.IntervalArray    => "interval[]",
            TypeOid.Inet             => "inet",
            TypeOid.InetArray        => "inet[]",
            TypeOid.Cidr             => "cidr",
            TypeOid.CidrArray        => "cidr[]",
            _                        => oid.ToString(CultureInfo.InvariantCulture)
        };
    }
    internal static Type ClrType(uint oid)
    {
        return (TypeOid)oid switch
        {
            TypeOid.Boolean => typeof(bool),
            TypeOid.Int16 => typeof(short),
            TypeOid.Int32 => typeof(int),
            TypeOid.Int64 or TypeOid.Money => typeof(long),
            TypeOid.Oid => typeof(uint),
            TypeOid.Float32 => typeof(float),
            TypeOid.Float64 => typeof(double),
            TypeOid.Numeric => typeof(PgNumeric),
            TypeOid.Uuid => typeof(Guid),
            TypeOid.Bytea => typeof(ReadOnlyMemory<byte>),
            TypeOid.Jsonb => typeof(Memory<byte>),
            TypeOid.Text or TypeOid.VarChar or TypeOid.BpChar or TypeOid.Name or TypeOid.Json or TypeOid.Xml => typeof(string),
            TypeOid.Date => typeof(PgDate),
            TypeOid.Time => typeof(PgTime),
            TypeOid.TimeTz => typeof(PgTimeTz),
            TypeOid.Timestamp => typeof(PgTimestamp),
            TypeOid.TimestampTz => typeof(PgTimestampTz),
            TypeOid.Interval => typeof(PgInterval),
            TypeOid.Inet or TypeOid.Cidr => typeof(PgInet),
            TypeOid.BooleanArray => typeof(ReadOnlyMemory<bool?>),
            TypeOid.Int16Array => typeof(ReadOnlyMemory<short?>),
            TypeOid.Int32Array => typeof(ReadOnlyMemory<int?>),
            TypeOid.Int64Array or TypeOid.MoneyArray => typeof(ReadOnlyMemory<long?>),
            TypeOid.OidArray => typeof(ReadOnlyMemory<uint?>),
            TypeOid.Float32Array => typeof(ReadOnlyMemory<float?>),
            TypeOid.Float64Array => typeof(ReadOnlyMemory<double?>),
            TypeOid.NumericArray => typeof(ReadOnlyMemory<PgNumeric?>),
            TypeOid.UuidArray => typeof(ReadOnlyMemory<Guid?>),
            TypeOid.ByteaArray => typeof(ReadOnlyMemory<ReadOnlyMemory<byte>?>),
            TypeOid.JsonbArray => typeof(ReadOnlyMemory<Memory<byte>?>),
            TypeOid.TextArray or TypeOid.VarCharArray or TypeOid.BpCharArray or TypeOid.NameArray or TypeOid.JsonArray or TypeOid.XmlArray => typeof(ReadOnlyMemory<string?>),
            TypeOid.DateArray => typeof(ReadOnlyMemory<PgDate?>),
            TypeOid.TimeArray => typeof(ReadOnlyMemory<PgTime?>),
            TypeOid.TimeTzArray => typeof(ReadOnlyMemory<PgTimeTz?>),
            TypeOid.TimestampArray => typeof(ReadOnlyMemory<PgTimestamp?>),
            TypeOid.TimestampTzArray => typeof(ReadOnlyMemory<PgTimestampTz?>),
            TypeOid.IntervalArray => typeof(ReadOnlyMemory<PgInterval?>),
            TypeOid.InetArray or TypeOid.CidrArray => typeof(ReadOnlyMemory<PgInet?>),
            _ => throw new NotSupportedException($"PostgreSQL OID {oid} has no object representation; use GetRawValue.")
        };
    }

    // Result descriptions are validated as binary before the session publishes them.
    internal static object Read(uint oid, ReadOnlySequence<byte> payload)
    {
        return (TypeOid)oid switch
        {
            TypeOid.Boolean                                                                                                                => FieldValueDecoder<bool>.Read(oid, payload),
            TypeOid.Int16                                                                                                                  => FieldValueDecoder<short>.Read(oid, payload),
            TypeOid.Int32                                                                                                                  => FieldValueDecoder<int>.Read(oid, payload),
            TypeOid.Int64 or TypeOid.Money                                                                                                 => FieldValueDecoder<long>.Read(oid, payload),
            TypeOid.Oid                                                                                                                    => FieldValueDecoder<uint>.Read(oid, payload),
            TypeOid.Float32                                                                                                                => FieldValueDecoder<float>.Read(oid, payload),
            TypeOid.Float64                                                                                                                => FieldValueDecoder<double>.Read(oid, payload),
            TypeOid.Numeric                                                                                                                => FieldValueDecoder<PgNumeric>.Read(oid, payload),
            TypeOid.Uuid                                                                                                                   => FieldValueDecoder<Guid>.Read(oid, payload),
            TypeOid.Bytea                                                                                                                  => FieldValueDecoder<ReadOnlyMemory<byte>>.Read(oid, payload),
            TypeOid.Jsonb                                                                                                                  => FieldValueDecoder<Memory<byte>>.Read(oid, payload),
            TypeOid.Text or TypeOid.VarChar or TypeOid.BpChar or TypeOid.Name or TypeOid.Json or TypeOid.Xml                               => FieldValueDecoder<string>.Read(oid, payload),
            TypeOid.Date                                                                                                                   => FieldValueDecoder<PgDate>.Read(oid, payload),
            TypeOid.Time                                                                                                                   => FieldValueDecoder<PgTime>.Read(oid, payload),
            TypeOid.TimeTz                                                                                                                 => FieldValueDecoder<PgTimeTz>.Read(oid, payload),
            TypeOid.Timestamp                                                                                                              => FieldValueDecoder<PgTimestamp>.Read(oid, payload),
            TypeOid.TimestampTz                                                                                                            => FieldValueDecoder<PgTimestampTz>.Read(oid, payload),
            TypeOid.Interval                                                                                                               => FieldValueDecoder<PgInterval>.Read(oid, payload),
            TypeOid.Inet or TypeOid.Cidr                                                                                                   => FieldValueDecoder<PgInet>.Read(oid, payload),
            TypeOid.BooleanArray                                                                                                           => FieldValueDecoder<ReadOnlyMemory<bool?>>.Read(oid, payload),
            TypeOid.Int16Array                                                                                                             => FieldValueDecoder<ReadOnlyMemory<short?>>.Read(oid, payload),
            TypeOid.Int32Array                                                                                                             => FieldValueDecoder<ReadOnlyMemory<int?>>.Read(oid, payload),
            TypeOid.Int64Array or TypeOid.MoneyArray                                                                                       => FieldValueDecoder<ReadOnlyMemory<long?>>.Read(oid, payload),
            TypeOid.OidArray                                                                                                               => FieldValueDecoder<ReadOnlyMemory<uint?>>.Read(oid, payload),
            TypeOid.Float32Array                                                                                                           => FieldValueDecoder<ReadOnlyMemory<float?>>.Read(oid, payload),
            TypeOid.Float64Array                                                                                                           => FieldValueDecoder<ReadOnlyMemory<double?>>.Read(oid, payload),
            TypeOid.NumericArray                                                                                                           => FieldValueDecoder<ReadOnlyMemory<PgNumeric?>>.Read(oid, payload),
            TypeOid.UuidArray                                                                                                              => FieldValueDecoder<ReadOnlyMemory<Guid?>>.Read(oid, payload),
            TypeOid.ByteaArray                                                                                                             => FieldValueDecoder<ReadOnlyMemory<ReadOnlyMemory<byte>?>>.Read(oid, payload),
            TypeOid.JsonbArray                                                                                                             => FieldValueDecoder<ReadOnlyMemory<Memory<byte>?>>.Read(oid, payload),
            TypeOid.TextArray or TypeOid.VarCharArray or TypeOid.BpCharArray or TypeOid.NameArray or TypeOid.JsonArray or TypeOid.XmlArray => FieldValueDecoder<ReadOnlyMemory<string?>>.Read(oid, payload),
            TypeOid.DateArray                                                                                                              => FieldValueDecoder<ReadOnlyMemory<PgDate?>>.Read(oid, payload),
            TypeOid.TimeArray                                                                                                              => FieldValueDecoder<ReadOnlyMemory<PgTime?>>.Read(oid, payload),
            TypeOid.TimeTzArray                                                                                                            => FieldValueDecoder<ReadOnlyMemory<PgTimeTz?>>.Read(oid, payload),
            TypeOid.TimestampArray                                                                                                         => FieldValueDecoder<ReadOnlyMemory<PgTimestamp?>>.Read(oid, payload),
            TypeOid.TimestampTzArray                                                                                                       => FieldValueDecoder<ReadOnlyMemory<PgTimestampTz?>>.Read(oid, payload),
            TypeOid.IntervalArray                                                                                                          => FieldValueDecoder<ReadOnlyMemory<PgInterval?>>.Read(oid, payload),
            TypeOid.InetArray or TypeOid.CidrArray                                                                                         => FieldValueDecoder<ReadOnlyMemory<PgInet?>>.Read(oid, payload),
            _                                                                                                                              => throw new NotSupportedException("Use GetRawValue for this PostgreSQL type.")
        };
    }
}