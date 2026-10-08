using System.Data;

namespace Mpgsql.Internal;

internal static class ParameterTypeMapping
{
    internal static uint ToOid(DbType type)
    {
        return (uint)(type switch
        {
            DbType.Boolean                                           => TypeOid.Boolean,
            DbType.Binary                                            => TypeOid.Bytea,
            DbType.Int16                                             => TypeOid.Int16,
            DbType.Int32                                             => TypeOid.Int32,
            DbType.Int64                                             => TypeOid.Int64,
            DbType.Single                                            => TypeOid.Float32,
            DbType.Double                                            => TypeOid.Float64,
            DbType.Decimal or DbType.VarNumeric                      => TypeOid.Numeric,
            DbType.String or DbType.AnsiString                       => TypeOid.Text,
            DbType.StringFixedLength or DbType.AnsiStringFixedLength => TypeOid.BpChar,
            DbType.Guid                                              => TypeOid.Uuid,
            DbType.Date                                              => TypeOid.Date,
            DbType.Time                                              => TypeOid.Time,
            DbType.DateTime or DbType.DateTime2                      => TypeOid.Timestamp,
            DbType.DateTimeOffset                                    => TypeOid.TimestampTz,
            DbType.Xml                                               => TypeOid.Xml,
            DbType.UInt32                                            => TypeOid.Oid,
            _                                                        => throw new NotSupportedException($"DbType {type} requires an explicit supported PostgreSQL OID.")
        });
    }
    internal static DbType ToDbType(uint oid)
    {
        return (TypeOid)oid switch
        {
            TypeOid.Boolean                                                 => DbType.Boolean,
            TypeOid.Bytea                                                   => DbType.Binary,
            TypeOid.Int16                                                   => DbType.Int16,
            TypeOid.Int32                                                   => DbType.Int32,
            TypeOid.Int64                                                   => DbType.Int64,
            TypeOid.Float32                                                 => DbType.Single,
            TypeOid.Float64                                                 => DbType.Double,
            TypeOid.Numeric                                                 => DbType.Decimal,
            TypeOid.Text or TypeOid.VarChar or TypeOid.Name or TypeOid.Json => DbType.String,
            TypeOid.BpChar                                                  => DbType.StringFixedLength,
            TypeOid.Uuid                                                    => DbType.Guid,
            TypeOid.Date                                                    => DbType.Date,
            TypeOid.Time                                                    => DbType.Time,
            TypeOid.Timestamp                                               => DbType.DateTime,
            TypeOid.TimestampTz                                             => DbType.DateTimeOffset,
            TypeOid.Xml                                                     => DbType.Xml,
            TypeOid.Oid                                                     => DbType.UInt32,
            _                                                               => DbType.Object
        };
    }
}