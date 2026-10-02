namespace Mpgsql;

/// <summary>Object identifiers for common PostgreSQL built-in types and their arrays.</summary>
// Source: https://github.com/postgres/postgres/blob/REL_17_STABLE/src/include/catalog/pg_type.dat
public enum TypeOid : uint
{
    Boolean = 16,
    BooleanArray = 1000,
    Bytea = 17,
    ByteaArray = 1001,

    Int16 = 21, // int2 / smallint
    Int16Array = 1005,
    Int32 = 23, // int4 / integer
    Int32Array = 1007,
    Int64 = 20, // int8 / bigint
    Int64Array = 1016,
    Float32 = 700, // float4 / real
    Float32Array = 1021,
    Float64 = 701, // float8 / double precision
    Float64Array = 1022,
    Numeric = 1700,
    NumericArray = 1231,
    Money = 790,
    MoneyArray = 791,

    Text = 25,
    TextArray = 1009,
    Uuid = 2950,
    UuidArray = 2951,
    Json = 114,
    JsonArray = 199,
    Jsonb = 3802,
    JsonbArray = 3807,
    Xml = 142,
    XmlArray = 143,

    Date = 1082,
    DateArray = 1182,
    Time = 1083,
    TimeArray = 1183,
    TimeTz = 1266,
    TimeTzArray = 1270,
    Timestamp = 1114,
    TimestampArray = 1115,
    TimestampTz = 1184,
    TimestampTzArray = 1185,
    Interval = 1186,
    IntervalArray = 1187,

    Inet = 869,
    InetArray = 1041,
    Cidr = 650,
    CidrArray = 651,
    Oid = 26,
    OidArray = 1028
}
