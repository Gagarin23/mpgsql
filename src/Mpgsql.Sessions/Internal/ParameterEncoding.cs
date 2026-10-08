using System.Runtime.CompilerServices;
using Mpgsql.Types;

namespace Mpgsql.Internal;

internal static class ParameterEncoding<T>
{
    internal static MpgsqlParameterValue Create(uint oid, T value)
    {
        if (oid == (uint)TypeOid.Int64 && typeof(T) == typeof(long))
        {
            return MpgsqlParameterValue.Int64(Unsafe.As<T, long>(ref value));
        }
        if (oid == (uint)TypeOid.Int64 && typeof(T) == typeof(long?))
        {
            return MpgsqlParameterValue.Int64(Unsafe.As<T, long?>(ref value));
        }
        if (oid == (uint)TypeOid.Int64Array && typeof(T) == typeof(ReadOnlyMemory<long>))
        {
            return MpgsqlParameterValue.Int64Array(Unsafe.As<T, ReadOnlyMemory<long>>(ref value));
        }
        if (oid == (uint)TypeOid.Int64Array && typeof(T) == typeof(ReadOnlyMemory<long>?))
        {
            return MpgsqlParameterValue.Int64Array(Unsafe.As<T, ReadOnlyMemory<long>?>(ref value));
        }
        if (oid == (uint)TypeOid.Int64Array && typeof(T) == typeof(long[]))
        {
            return MpgsqlParameterValue.Int64Array(Unsafe.As<T, long[]>(ref value));
        }
        if (oid == (uint)TypeOid.Int64Array && typeof(T) == typeof(ReadOnlyMemory<long?>))
        {
            return MpgsqlParameterValue.NullableInt64Array(Unsafe.As<T, ReadOnlyMemory<long?>>(ref value));
        }
        if (oid == (uint)TypeOid.Int64Array && typeof(T) == typeof(ReadOnlyMemory<long?>?))
        {
            return MpgsqlParameterValue.NullableInt64Array(Unsafe.As<T, ReadOnlyMemory<long?>?>(ref value));
        }
        if (oid == (uint)TypeOid.Int64Array && typeof(T) == typeof(long?[]))
        {
            return MpgsqlParameterValue.NullableInt64Array(Unsafe.As<T, long?[]>(ref value));
        }
        if (oid == (uint)TypeOid.Boolean && typeof(T) == typeof(bool))
        {
            return MpgsqlParameterValue.Boolean(Unsafe.As<T, bool>(ref value));
        }
        if (oid == (uint)TypeOid.Boolean && typeof(T) == typeof(bool?))
        {
            return MpgsqlParameterValue.Boolean(Unsafe.As<T, bool?>(ref value));
        }
        if (oid == (uint)TypeOid.BooleanArray && typeof(T) == typeof(ReadOnlyMemory<bool>))
        {
            return MpgsqlParameterValue.BooleanArray(Unsafe.As<T, ReadOnlyMemory<bool>>(ref value));
        }
        if (oid == (uint)TypeOid.BooleanArray && typeof(T) == typeof(ReadOnlyMemory<bool>?))
        {
            return MpgsqlParameterValue.BooleanArray(Unsafe.As<T, ReadOnlyMemory<bool>?>(ref value));
        }
        if (oid == (uint)TypeOid.BooleanArray && typeof(T) == typeof(bool[]))
        {
            return MpgsqlParameterValue.BooleanArray(Unsafe.As<T, bool[]>(ref value));
        }
        if (oid == (uint)TypeOid.BooleanArray && typeof(T) == typeof(ReadOnlyMemory<bool?>))
        {
            return MpgsqlParameterValue.NullableBooleanArray(Unsafe.As<T, ReadOnlyMemory<bool?>>(ref value));
        }
        if (oid == (uint)TypeOid.BooleanArray && typeof(T) == typeof(ReadOnlyMemory<bool?>?))
        {
            return MpgsqlParameterValue.NullableBooleanArray(Unsafe.As<T, ReadOnlyMemory<bool?>?>(ref value));
        }
        if (oid == (uint)TypeOid.BooleanArray && typeof(T) == typeof(bool?[]))
        {
            return MpgsqlParameterValue.NullableBooleanArray(Unsafe.As<T, bool?[]>(ref value));
        }
        if (oid == (uint)TypeOid.Bytea && typeof(T) == typeof(ReadOnlyMemory<byte>))
        {
            return MpgsqlParameterValue.Bytea(Unsafe.As<T, ReadOnlyMemory<byte>>(ref value));
        }
        if (oid == (uint)TypeOid.Bytea && typeof(T) == typeof(ReadOnlyMemory<byte>?))
        {
            return MpgsqlParameterValue.Bytea(Unsafe.As<T, ReadOnlyMemory<byte>?>(ref value));
        }
        if (oid == (uint)TypeOid.Bytea && typeof(T) == typeof(byte[]))
        {
            return MpgsqlParameterValue.Bytea(Unsafe.As<T, byte[]>(ref value));
        }
        if (oid == (uint)TypeOid.ByteaArray && typeof(T) == typeof(ReadOnlyMemory<ReadOnlyMemory<byte>>))
        {
            return MpgsqlParameterValue.ByteaArray(Unsafe.As<T, ReadOnlyMemory<ReadOnlyMemory<byte>>>(ref value));
        }
        if (oid == (uint)TypeOid.ByteaArray && typeof(T) == typeof(ReadOnlyMemory<ReadOnlyMemory<byte>>?))
        {
            return MpgsqlParameterValue.ByteaArray(Unsafe.As<T, ReadOnlyMemory<ReadOnlyMemory<byte>>?>(ref value));
        }
        if (oid == (uint)TypeOid.ByteaArray && typeof(T) == typeof(ReadOnlyMemory<byte>[]))
        {
            return MpgsqlParameterValue.ByteaArray(Unsafe.As<T, ReadOnlyMemory<byte>[]>(ref value));
        }
        if (oid == (uint)TypeOid.ByteaArray && typeof(T) == typeof(ReadOnlyMemory<ReadOnlyMemory<byte>?>))
        {
            return MpgsqlParameterValue.NullableByteaArray(Unsafe.As<T, ReadOnlyMemory<ReadOnlyMemory<byte>?>>(ref value));
        }
        if (oid == (uint)TypeOid.ByteaArray && typeof(T) == typeof(ReadOnlyMemory<ReadOnlyMemory<byte>?>?))
        {
            return MpgsqlParameterValue.NullableByteaArray(Unsafe.As<T, ReadOnlyMemory<ReadOnlyMemory<byte>?>?>(ref value));
        }
        if (oid == (uint)TypeOid.ByteaArray && typeof(T) == typeof(ReadOnlyMemory<byte>?[]))
        {
            return MpgsqlParameterValue.NullableByteaArray(Unsafe.As<T, ReadOnlyMemory<byte>?[]>(ref value));
        }
        if (oid == (uint)TypeOid.Int16 && typeof(T) == typeof(short))
        {
            return MpgsqlParameterValue.Int16(Unsafe.As<T, short>(ref value));
        }
        if (oid == (uint)TypeOid.Int16 && typeof(T) == typeof(short?))
        {
            return MpgsqlParameterValue.Int16(Unsafe.As<T, short?>(ref value));
        }
        if (oid == (uint)TypeOid.Int16Array && typeof(T) == typeof(ReadOnlyMemory<short>))
        {
            return MpgsqlParameterValue.Int16Array(Unsafe.As<T, ReadOnlyMemory<short>>(ref value));
        }
        if (oid == (uint)TypeOid.Int16Array && typeof(T) == typeof(ReadOnlyMemory<short>?))
        {
            return MpgsqlParameterValue.Int16Array(Unsafe.As<T, ReadOnlyMemory<short>?>(ref value));
        }
        if (oid == (uint)TypeOid.Int16Array && typeof(T) == typeof(short[]))
        {
            return MpgsqlParameterValue.Int16Array(Unsafe.As<T, short[]>(ref value));
        }
        if (oid == (uint)TypeOid.Int16Array && typeof(T) == typeof(ReadOnlyMemory<short?>))
        {
            return MpgsqlParameterValue.NullableInt16Array(Unsafe.As<T, ReadOnlyMemory<short?>>(ref value));
        }
        if (oid == (uint)TypeOid.Int16Array && typeof(T) == typeof(ReadOnlyMemory<short?>?))
        {
            return MpgsqlParameterValue.NullableInt16Array(Unsafe.As<T, ReadOnlyMemory<short?>?>(ref value));
        }
        if (oid == (uint)TypeOid.Int16Array && typeof(T) == typeof(short?[]))
        {
            return MpgsqlParameterValue.NullableInt16Array(Unsafe.As<T, short?[]>(ref value));
        }
        if (oid == (uint)TypeOid.Int32 && typeof(T) == typeof(int))
        {
            return MpgsqlParameterValue.Int32(Unsafe.As<T, int>(ref value));
        }
        if (oid == (uint)TypeOid.Int32 && typeof(T) == typeof(int?))
        {
            return MpgsqlParameterValue.Int32(Unsafe.As<T, int?>(ref value));
        }
        if (oid == (uint)TypeOid.Int32Array && typeof(T) == typeof(ReadOnlyMemory<int>))
        {
            return MpgsqlParameterValue.Int32Array(Unsafe.As<T, ReadOnlyMemory<int>>(ref value));
        }
        if (oid == (uint)TypeOid.Int32Array && typeof(T) == typeof(ReadOnlyMemory<int>?))
        {
            return MpgsqlParameterValue.Int32Array(Unsafe.As<T, ReadOnlyMemory<int>?>(ref value));
        }
        if (oid == (uint)TypeOid.Int32Array && typeof(T) == typeof(int[]))
        {
            return MpgsqlParameterValue.Int32Array(Unsafe.As<T, int[]>(ref value));
        }
        if (oid == (uint)TypeOid.Int32Array && typeof(T) == typeof(ReadOnlyMemory<int?>))
        {
            return MpgsqlParameterValue.NullableInt32Array(Unsafe.As<T, ReadOnlyMemory<int?>>(ref value));
        }
        if (oid == (uint)TypeOid.Int32Array && typeof(T) == typeof(ReadOnlyMemory<int?>?))
        {
            return MpgsqlParameterValue.NullableInt32Array(Unsafe.As<T, ReadOnlyMemory<int?>?>(ref value));
        }
        if (oid == (uint)TypeOid.Int32Array && typeof(T) == typeof(int?[]))
        {
            return MpgsqlParameterValue.NullableInt32Array(Unsafe.As<T, int?[]>(ref value));
        }
        if (oid == (uint)TypeOid.Float32 && typeof(T) == typeof(float))
        {
            return MpgsqlParameterValue.Float32(Unsafe.As<T, float>(ref value));
        }
        if (oid == (uint)TypeOid.Float32 && typeof(T) == typeof(float?))
        {
            return MpgsqlParameterValue.Float32(Unsafe.As<T, float?>(ref value));
        }
        if (oid == (uint)TypeOid.Float32Array && typeof(T) == typeof(ReadOnlyMemory<float>))
        {
            return MpgsqlParameterValue.Float32Array(Unsafe.As<T, ReadOnlyMemory<float>>(ref value));
        }
        if (oid == (uint)TypeOid.Float32Array && typeof(T) == typeof(ReadOnlyMemory<float>?))
        {
            return MpgsqlParameterValue.Float32Array(Unsafe.As<T, ReadOnlyMemory<float>?>(ref value));
        }
        if (oid == (uint)TypeOid.Float32Array && typeof(T) == typeof(float[]))
        {
            return MpgsqlParameterValue.Float32Array(Unsafe.As<T, float[]>(ref value));
        }
        if (oid == (uint)TypeOid.Float32Array && typeof(T) == typeof(ReadOnlyMemory<float?>))
        {
            return MpgsqlParameterValue.NullableFloat32Array(Unsafe.As<T, ReadOnlyMemory<float?>>(ref value));
        }
        if (oid == (uint)TypeOid.Float32Array && typeof(T) == typeof(ReadOnlyMemory<float?>?))
        {
            return MpgsqlParameterValue.NullableFloat32Array(Unsafe.As<T, ReadOnlyMemory<float?>?>(ref value));
        }
        if (oid == (uint)TypeOid.Float32Array && typeof(T) == typeof(float?[]))
        {
            return MpgsqlParameterValue.NullableFloat32Array(Unsafe.As<T, float?[]>(ref value));
        }
        if (oid == (uint)TypeOid.Float64 && typeof(T) == typeof(double))
        {
            return MpgsqlParameterValue.Float64(Unsafe.As<T, double>(ref value));
        }
        if (oid == (uint)TypeOid.Float64 && typeof(T) == typeof(double?))
        {
            return MpgsqlParameterValue.Float64(Unsafe.As<T, double?>(ref value));
        }
        if (oid == (uint)TypeOid.Float64Array && typeof(T) == typeof(ReadOnlyMemory<double>))
        {
            return MpgsqlParameterValue.Float64Array(Unsafe.As<T, ReadOnlyMemory<double>>(ref value));
        }
        if (oid == (uint)TypeOid.Float64Array && typeof(T) == typeof(ReadOnlyMemory<double>?))
        {
            return MpgsqlParameterValue.Float64Array(Unsafe.As<T, ReadOnlyMemory<double>?>(ref value));
        }
        if (oid == (uint)TypeOid.Float64Array && typeof(T) == typeof(double[]))
        {
            return MpgsqlParameterValue.Float64Array(Unsafe.As<T, double[]>(ref value));
        }
        if (oid == (uint)TypeOid.Float64Array && typeof(T) == typeof(ReadOnlyMemory<double?>))
        {
            return MpgsqlParameterValue.NullableFloat64Array(Unsafe.As<T, ReadOnlyMemory<double?>>(ref value));
        }
        if (oid == (uint)TypeOid.Float64Array && typeof(T) == typeof(ReadOnlyMemory<double?>?))
        {
            return MpgsqlParameterValue.NullableFloat64Array(Unsafe.As<T, ReadOnlyMemory<double?>?>(ref value));
        }
        if (oid == (uint)TypeOid.Float64Array && typeof(T) == typeof(double?[]))
        {
            return MpgsqlParameterValue.NullableFloat64Array(Unsafe.As<T, double?[]>(ref value));
        }
        if (oid == (uint)TypeOid.Numeric && typeof(T) == typeof(PgNumeric))
        {
            return MpgsqlParameterValue.Numeric(Unsafe.As<T, PgNumeric>(ref value));
        }
        if (oid == (uint)TypeOid.Numeric && typeof(T) == typeof(PgNumeric?))
        {
            return MpgsqlParameterValue.Numeric(Unsafe.As<T, PgNumeric?>(ref value));
        }
        if (oid == (uint)TypeOid.NumericArray && typeof(T) == typeof(ReadOnlyMemory<PgNumeric>))
        {
            return MpgsqlParameterValue.NumericArray(Unsafe.As<T, ReadOnlyMemory<PgNumeric>>(ref value));
        }
        if (oid == (uint)TypeOid.NumericArray && typeof(T) == typeof(ReadOnlyMemory<PgNumeric>?))
        {
            return MpgsqlParameterValue.NumericArray(Unsafe.As<T, ReadOnlyMemory<PgNumeric>?>(ref value));
        }
        if (oid == (uint)TypeOid.NumericArray && typeof(T) == typeof(PgNumeric[]))
        {
            return MpgsqlParameterValue.NumericArray(Unsafe.As<T, PgNumeric[]>(ref value));
        }
        if (oid == (uint)TypeOid.NumericArray && typeof(T) == typeof(ReadOnlyMemory<PgNumeric?>))
        {
            return MpgsqlParameterValue.NullableNumericArray(Unsafe.As<T, ReadOnlyMemory<PgNumeric?>>(ref value));
        }
        if (oid == (uint)TypeOid.NumericArray && typeof(T) == typeof(ReadOnlyMemory<PgNumeric?>?))
        {
            return MpgsqlParameterValue.NullableNumericArray(Unsafe.As<T, ReadOnlyMemory<PgNumeric?>?>(ref value));
        }
        if (oid == (uint)TypeOid.NumericArray && typeof(T) == typeof(PgNumeric?[]))
        {
            return MpgsqlParameterValue.NullableNumericArray(Unsafe.As<T, PgNumeric?[]>(ref value));
        }
        if (oid == (uint)TypeOid.Money && typeof(T) == typeof(long))
        {
            return MpgsqlParameterValue.Money(Unsafe.As<T, long>(ref value));
        }
        if (oid == (uint)TypeOid.Money && typeof(T) == typeof(long?))
        {
            return MpgsqlParameterValue.Money(Unsafe.As<T, long?>(ref value));
        }
        if (oid == (uint)TypeOid.MoneyArray && typeof(T) == typeof(ReadOnlyMemory<long>))
        {
            return MpgsqlParameterValue.MoneyArray(Unsafe.As<T, ReadOnlyMemory<long>>(ref value));
        }
        if (oid == (uint)TypeOid.MoneyArray && typeof(T) == typeof(ReadOnlyMemory<long>?))
        {
            return MpgsqlParameterValue.MoneyArray(Unsafe.As<T, ReadOnlyMemory<long>?>(ref value));
        }
        if (oid == (uint)TypeOid.MoneyArray && typeof(T) == typeof(long[]))
        {
            return MpgsqlParameterValue.MoneyArray(Unsafe.As<T, long[]>(ref value));
        }
        if (oid == (uint)TypeOid.MoneyArray && typeof(T) == typeof(ReadOnlyMemory<long?>))
        {
            return MpgsqlParameterValue.NullableMoneyArray(Unsafe.As<T, ReadOnlyMemory<long?>>(ref value));
        }
        if (oid == (uint)TypeOid.MoneyArray && typeof(T) == typeof(ReadOnlyMemory<long?>?))
        {
            return MpgsqlParameterValue.NullableMoneyArray(Unsafe.As<T, ReadOnlyMemory<long?>?>(ref value));
        }
        if (oid == (uint)TypeOid.MoneyArray && typeof(T) == typeof(long?[]))
        {
            return MpgsqlParameterValue.NullableMoneyArray(Unsafe.As<T, long?[]>(ref value));
        }
        if (oid == (uint)TypeOid.Text && typeof(T) == typeof(string))
        {
            return MpgsqlParameterValue.Text(Unsafe.As<T, string>(ref value));
        }
        if (oid == (uint)TypeOid.TextArray && typeof(T) == typeof(ReadOnlyMemory<string?>))
        {
            return MpgsqlParameterValue.TextArray(Unsafe.As<T, ReadOnlyMemory<string?>>(ref value));
        }
        if (oid == (uint)TypeOid.TextArray && typeof(T) == typeof(ReadOnlyMemory<string?>?))
        {
            return MpgsqlParameterValue.TextArray(Unsafe.As<T, ReadOnlyMemory<string?>?>(ref value));
        }
        if (oid == (uint)TypeOid.TextArray && typeof(T) == typeof(string?[]))
        {
            return MpgsqlParameterValue.TextArray(Unsafe.As<T, string?[]>(ref value));
        }
        if (oid == (uint)TypeOid.VarChar && typeof(T) == typeof(string))
        {
            return MpgsqlParameterValue.VarChar(Unsafe.As<T, string>(ref value));
        }
        if (oid == (uint)TypeOid.VarCharArray && typeof(T) == typeof(ReadOnlyMemory<string?>))
        {
            return MpgsqlParameterValue.VarCharArray(Unsafe.As<T, ReadOnlyMemory<string?>>(ref value));
        }
        if (oid == (uint)TypeOid.VarCharArray && typeof(T) == typeof(ReadOnlyMemory<string?>?))
        {
            return MpgsqlParameterValue.VarCharArray(Unsafe.As<T, ReadOnlyMemory<string?>?>(ref value));
        }
        if (oid == (uint)TypeOid.VarCharArray && typeof(T) == typeof(string?[]))
        {
            return MpgsqlParameterValue.VarCharArray(Unsafe.As<T, string?[]>(ref value));
        }
        if (oid == (uint)TypeOid.BpChar && typeof(T) == typeof(string))
        {
            return MpgsqlParameterValue.BpChar(Unsafe.As<T, string>(ref value));
        }
        if (oid == (uint)TypeOid.BpCharArray && typeof(T) == typeof(ReadOnlyMemory<string?>))
        {
            return MpgsqlParameterValue.BpCharArray(Unsafe.As<T, ReadOnlyMemory<string?>>(ref value));
        }
        if (oid == (uint)TypeOid.BpCharArray && typeof(T) == typeof(ReadOnlyMemory<string?>?))
        {
            return MpgsqlParameterValue.BpCharArray(Unsafe.As<T, ReadOnlyMemory<string?>?>(ref value));
        }
        if (oid == (uint)TypeOid.BpCharArray && typeof(T) == typeof(string?[]))
        {
            return MpgsqlParameterValue.BpCharArray(Unsafe.As<T, string?[]>(ref value));
        }
        if (oid == (uint)TypeOid.Name && typeof(T) == typeof(string))
        {
            return MpgsqlParameterValue.Name(Unsafe.As<T, string>(ref value));
        }
        if (oid == (uint)TypeOid.NameArray && typeof(T) == typeof(ReadOnlyMemory<string?>))
        {
            return MpgsqlParameterValue.NameArray(Unsafe.As<T, ReadOnlyMemory<string?>>(ref value));
        }
        if (oid == (uint)TypeOid.NameArray && typeof(T) == typeof(ReadOnlyMemory<string?>?))
        {
            return MpgsqlParameterValue.NameArray(Unsafe.As<T, ReadOnlyMemory<string?>?>(ref value));
        }
        if (oid == (uint)TypeOid.NameArray && typeof(T) == typeof(string?[]))
        {
            return MpgsqlParameterValue.NameArray(Unsafe.As<T, string?[]>(ref value));
        }
        if (oid == (uint)TypeOid.Uuid && typeof(T) == typeof(Guid))
        {
            return MpgsqlParameterValue.Uuid(Unsafe.As<T, Guid>(ref value));
        }
        if (oid == (uint)TypeOid.Uuid && typeof(T) == typeof(Guid?))
        {
            return MpgsqlParameterValue.Uuid(Unsafe.As<T, Guid?>(ref value));
        }
        if (oid == (uint)TypeOid.UuidArray && typeof(T) == typeof(ReadOnlyMemory<Guid>))
        {
            return MpgsqlParameterValue.UuidArray(Unsafe.As<T, ReadOnlyMemory<Guid>>(ref value));
        }
        if (oid == (uint)TypeOid.UuidArray && typeof(T) == typeof(ReadOnlyMemory<Guid>?))
        {
            return MpgsqlParameterValue.UuidArray(Unsafe.As<T, ReadOnlyMemory<Guid>?>(ref value));
        }
        if (oid == (uint)TypeOid.UuidArray && typeof(T) == typeof(Guid[]))
        {
            return MpgsqlParameterValue.UuidArray(Unsafe.As<T, Guid[]>(ref value));
        }
        if (oid == (uint)TypeOid.UuidArray && typeof(T) == typeof(ReadOnlyMemory<Guid?>))
        {
            return MpgsqlParameterValue.NullableUuidArray(Unsafe.As<T, ReadOnlyMemory<Guid?>>(ref value));
        }
        if (oid == (uint)TypeOid.UuidArray && typeof(T) == typeof(ReadOnlyMemory<Guid?>?))
        {
            return MpgsqlParameterValue.NullableUuidArray(Unsafe.As<T, ReadOnlyMemory<Guid?>?>(ref value));
        }
        if (oid == (uint)TypeOid.UuidArray && typeof(T) == typeof(Guid?[]))
        {
            return MpgsqlParameterValue.NullableUuidArray(Unsafe.As<T, Guid?[]>(ref value));
        }
        if (oid == (uint)TypeOid.Json && typeof(T) == typeof(string))
        {
            return MpgsqlParameterValue.Json(Unsafe.As<T, string>(ref value));
        }
        if (oid == (uint)TypeOid.JsonArray && typeof(T) == typeof(ReadOnlyMemory<string?>))
        {
            return MpgsqlParameterValue.JsonArray(Unsafe.As<T, ReadOnlyMemory<string?>>(ref value));
        }
        if (oid == (uint)TypeOid.JsonArray && typeof(T) == typeof(ReadOnlyMemory<string?>?))
        {
            return MpgsqlParameterValue.JsonArray(Unsafe.As<T, ReadOnlyMemory<string?>?>(ref value));
        }
        if (oid == (uint)TypeOid.JsonArray && typeof(T) == typeof(string?[]))
        {
            return MpgsqlParameterValue.JsonArray(Unsafe.As<T, string?[]>(ref value));
        }
        if (oid == (uint)TypeOid.Jsonb && typeof(T) == typeof(Memory<byte>))
        {
            return MpgsqlParameterValue.Jsonb(Unsafe.As<T, Memory<byte>>(ref value));
        }
        if (oid == (uint)TypeOid.Jsonb && typeof(T) == typeof(Memory<byte>?))
        {
            return MpgsqlParameterValue.Jsonb(Unsafe.As<T, Memory<byte>?>(ref value));
        }
        if (oid == (uint)TypeOid.JsonbArray && typeof(T) == typeof(ReadOnlyMemory<Memory<byte>>))
        {
            return MpgsqlParameterValue.JsonbArray(Unsafe.As<T, ReadOnlyMemory<Memory<byte>>>(ref value));
        }
        if (oid == (uint)TypeOid.JsonbArray && typeof(T) == typeof(ReadOnlyMemory<Memory<byte>>?))
        {
            return MpgsqlParameterValue.JsonbArray(Unsafe.As<T, ReadOnlyMemory<Memory<byte>>?>(ref value));
        }
        if (oid == (uint)TypeOid.JsonbArray && typeof(T) == typeof(Memory<byte>[]))
        {
            return MpgsqlParameterValue.JsonbArray(Unsafe.As<T, Memory<byte>[]>(ref value));
        }
        if (oid == (uint)TypeOid.JsonbArray && typeof(T) == typeof(ReadOnlyMemory<Memory<byte>?>))
        {
            return MpgsqlParameterValue.NullableJsonbArray(Unsafe.As<T, ReadOnlyMemory<Memory<byte>?>>(ref value));
        }
        if (oid == (uint)TypeOid.JsonbArray && typeof(T) == typeof(ReadOnlyMemory<Memory<byte>?>?))
        {
            return MpgsqlParameterValue.NullableJsonbArray(Unsafe.As<T, ReadOnlyMemory<Memory<byte>?>?>(ref value));
        }
        if (oid == (uint)TypeOid.JsonbArray && typeof(T) == typeof(Memory<byte>?[]))
        {
            return MpgsqlParameterValue.NullableJsonbArray(Unsafe.As<T, Memory<byte>?[]>(ref value));
        }
        if (oid == (uint)TypeOid.Xml && typeof(T) == typeof(string))
        {
            return MpgsqlParameterValue.Xml(Unsafe.As<T, string>(ref value));
        }
        if (oid == (uint)TypeOid.XmlArray && typeof(T) == typeof(ReadOnlyMemory<string?>))
        {
            return MpgsqlParameterValue.XmlArray(Unsafe.As<T, ReadOnlyMemory<string?>>(ref value));
        }
        if (oid == (uint)TypeOid.XmlArray && typeof(T) == typeof(ReadOnlyMemory<string?>?))
        {
            return MpgsqlParameterValue.XmlArray(Unsafe.As<T, ReadOnlyMemory<string?>?>(ref value));
        }
        if (oid == (uint)TypeOid.XmlArray && typeof(T) == typeof(string?[]))
        {
            return MpgsqlParameterValue.XmlArray(Unsafe.As<T, string?[]>(ref value));
        }
        if (oid == (uint)TypeOid.Date && typeof(T) == typeof(PgDate))
        {
            return MpgsqlParameterValue.Date(Unsafe.As<T, PgDate>(ref value));
        }
        if (oid == (uint)TypeOid.Date && typeof(T) == typeof(PgDate?))
        {
            return MpgsqlParameterValue.Date(Unsafe.As<T, PgDate?>(ref value));
        }
        if (oid == (uint)TypeOid.DateArray && typeof(T) == typeof(ReadOnlyMemory<PgDate>))
        {
            return MpgsqlParameterValue.DateArray(Unsafe.As<T, ReadOnlyMemory<PgDate>>(ref value));
        }
        if (oid == (uint)TypeOid.DateArray && typeof(T) == typeof(ReadOnlyMemory<PgDate>?))
        {
            return MpgsqlParameterValue.DateArray(Unsafe.As<T, ReadOnlyMemory<PgDate>?>(ref value));
        }
        if (oid == (uint)TypeOid.DateArray && typeof(T) == typeof(PgDate[]))
        {
            return MpgsqlParameterValue.DateArray(Unsafe.As<T, PgDate[]>(ref value));
        }
        if (oid == (uint)TypeOid.DateArray && typeof(T) == typeof(ReadOnlyMemory<PgDate?>))
        {
            return MpgsqlParameterValue.NullableDateArray(Unsafe.As<T, ReadOnlyMemory<PgDate?>>(ref value));
        }
        if (oid == (uint)TypeOid.DateArray && typeof(T) == typeof(ReadOnlyMemory<PgDate?>?))
        {
            return MpgsqlParameterValue.NullableDateArray(Unsafe.As<T, ReadOnlyMemory<PgDate?>?>(ref value));
        }
        if (oid == (uint)TypeOid.DateArray && typeof(T) == typeof(PgDate?[]))
        {
            return MpgsqlParameterValue.NullableDateArray(Unsafe.As<T, PgDate?[]>(ref value));
        }
        if (oid == (uint)TypeOid.Time && typeof(T) == typeof(PgTime))
        {
            return MpgsqlParameterValue.Time(Unsafe.As<T, PgTime>(ref value));
        }
        if (oid == (uint)TypeOid.Time && typeof(T) == typeof(PgTime?))
        {
            return MpgsqlParameterValue.Time(Unsafe.As<T, PgTime?>(ref value));
        }
        if (oid == (uint)TypeOid.TimeArray && typeof(T) == typeof(ReadOnlyMemory<PgTime>))
        {
            return MpgsqlParameterValue.TimeArray(Unsafe.As<T, ReadOnlyMemory<PgTime>>(ref value));
        }
        if (oid == (uint)TypeOid.TimeArray && typeof(T) == typeof(ReadOnlyMemory<PgTime>?))
        {
            return MpgsqlParameterValue.TimeArray(Unsafe.As<T, ReadOnlyMemory<PgTime>?>(ref value));
        }
        if (oid == (uint)TypeOid.TimeArray && typeof(T) == typeof(PgTime[]))
        {
            return MpgsqlParameterValue.TimeArray(Unsafe.As<T, PgTime[]>(ref value));
        }
        if (oid == (uint)TypeOid.TimeArray && typeof(T) == typeof(ReadOnlyMemory<PgTime?>))
        {
            return MpgsqlParameterValue.NullableTimeArray(Unsafe.As<T, ReadOnlyMemory<PgTime?>>(ref value));
        }
        if (oid == (uint)TypeOid.TimeArray && typeof(T) == typeof(ReadOnlyMemory<PgTime?>?))
        {
            return MpgsqlParameterValue.NullableTimeArray(Unsafe.As<T, ReadOnlyMemory<PgTime?>?>(ref value));
        }
        if (oid == (uint)TypeOid.TimeArray && typeof(T) == typeof(PgTime?[]))
        {
            return MpgsqlParameterValue.NullableTimeArray(Unsafe.As<T, PgTime?[]>(ref value));
        }
        if (oid == (uint)TypeOid.TimeTz && typeof(T) == typeof(PgTimeTz))
        {
            return MpgsqlParameterValue.TimeTz(Unsafe.As<T, PgTimeTz>(ref value));
        }
        if (oid == (uint)TypeOid.TimeTz && typeof(T) == typeof(PgTimeTz?))
        {
            return MpgsqlParameterValue.TimeTz(Unsafe.As<T, PgTimeTz?>(ref value));
        }
        if (oid == (uint)TypeOid.TimeTzArray && typeof(T) == typeof(ReadOnlyMemory<PgTimeTz>))
        {
            return MpgsqlParameterValue.TimeTzArray(Unsafe.As<T, ReadOnlyMemory<PgTimeTz>>(ref value));
        }
        if (oid == (uint)TypeOid.TimeTzArray && typeof(T) == typeof(ReadOnlyMemory<PgTimeTz>?))
        {
            return MpgsqlParameterValue.TimeTzArray(Unsafe.As<T, ReadOnlyMemory<PgTimeTz>?>(ref value));
        }
        if (oid == (uint)TypeOid.TimeTzArray && typeof(T) == typeof(PgTimeTz[]))
        {
            return MpgsqlParameterValue.TimeTzArray(Unsafe.As<T, PgTimeTz[]>(ref value));
        }
        if (oid == (uint)TypeOid.TimeTzArray && typeof(T) == typeof(ReadOnlyMemory<PgTimeTz?>))
        {
            return MpgsqlParameterValue.NullableTimeTzArray(Unsafe.As<T, ReadOnlyMemory<PgTimeTz?>>(ref value));
        }
        if (oid == (uint)TypeOid.TimeTzArray && typeof(T) == typeof(ReadOnlyMemory<PgTimeTz?>?))
        {
            return MpgsqlParameterValue.NullableTimeTzArray(Unsafe.As<T, ReadOnlyMemory<PgTimeTz?>?>(ref value));
        }
        if (oid == (uint)TypeOid.TimeTzArray && typeof(T) == typeof(PgTimeTz?[]))
        {
            return MpgsqlParameterValue.NullableTimeTzArray(Unsafe.As<T, PgTimeTz?[]>(ref value));
        }
        if (oid == (uint)TypeOid.Timestamp && typeof(T) == typeof(PgTimestamp))
        {
            return MpgsqlParameterValue.Timestamp(Unsafe.As<T, PgTimestamp>(ref value));
        }
        if (oid == (uint)TypeOid.Timestamp && typeof(T) == typeof(PgTimestamp?))
        {
            return MpgsqlParameterValue.Timestamp(Unsafe.As<T, PgTimestamp?>(ref value));
        }
        if (oid == (uint)TypeOid.TimestampArray && typeof(T) == typeof(ReadOnlyMemory<PgTimestamp>))
        {
            return MpgsqlParameterValue.TimestampArray(Unsafe.As<T, ReadOnlyMemory<PgTimestamp>>(ref value));
        }
        if (oid == (uint)TypeOid.TimestampArray && typeof(T) == typeof(ReadOnlyMemory<PgTimestamp>?))
        {
            return MpgsqlParameterValue.TimestampArray(Unsafe.As<T, ReadOnlyMemory<PgTimestamp>?>(ref value));
        }
        if (oid == (uint)TypeOid.TimestampArray && typeof(T) == typeof(PgTimestamp[]))
        {
            return MpgsqlParameterValue.TimestampArray(Unsafe.As<T, PgTimestamp[]>(ref value));
        }
        if (oid == (uint)TypeOid.TimestampArray && typeof(T) == typeof(ReadOnlyMemory<PgTimestamp?>))
        {
            return MpgsqlParameterValue.NullableTimestampArray(Unsafe.As<T, ReadOnlyMemory<PgTimestamp?>>(ref value));
        }
        if (oid == (uint)TypeOid.TimestampArray && typeof(T) == typeof(ReadOnlyMemory<PgTimestamp?>?))
        {
            return MpgsqlParameterValue.NullableTimestampArray(Unsafe.As<T, ReadOnlyMemory<PgTimestamp?>?>(ref value));
        }
        if (oid == (uint)TypeOid.TimestampArray && typeof(T) == typeof(PgTimestamp?[]))
        {
            return MpgsqlParameterValue.NullableTimestampArray(Unsafe.As<T, PgTimestamp?[]>(ref value));
        }
        if (oid == (uint)TypeOid.TimestampTz && typeof(T) == typeof(PgTimestampTz))
        {
            return MpgsqlParameterValue.TimestampTz(Unsafe.As<T, PgTimestampTz>(ref value));
        }
        if (oid == (uint)TypeOid.TimestampTz && typeof(T) == typeof(PgTimestampTz?))
        {
            return MpgsqlParameterValue.TimestampTz(Unsafe.As<T, PgTimestampTz?>(ref value));
        }
        if (oid == (uint)TypeOid.TimestampTzArray && typeof(T) == typeof(ReadOnlyMemory<PgTimestampTz>))
        {
            return MpgsqlParameterValue.TimestampTzArray(Unsafe.As<T, ReadOnlyMemory<PgTimestampTz>>(ref value));
        }
        if (oid == (uint)TypeOid.TimestampTzArray && typeof(T) == typeof(ReadOnlyMemory<PgTimestampTz>?))
        {
            return MpgsqlParameterValue.TimestampTzArray(Unsafe.As<T, ReadOnlyMemory<PgTimestampTz>?>(ref value));
        }
        if (oid == (uint)TypeOid.TimestampTzArray && typeof(T) == typeof(PgTimestampTz[]))
        {
            return MpgsqlParameterValue.TimestampTzArray(Unsafe.As<T, PgTimestampTz[]>(ref value));
        }
        if (oid == (uint)TypeOid.TimestampTzArray && typeof(T) == typeof(ReadOnlyMemory<PgTimestampTz?>))
        {
            return MpgsqlParameterValue.NullableTimestampTzArray(Unsafe.As<T, ReadOnlyMemory<PgTimestampTz?>>(ref value));
        }
        if (oid == (uint)TypeOid.TimestampTzArray && typeof(T) == typeof(ReadOnlyMemory<PgTimestampTz?>?))
        {
            return MpgsqlParameterValue.NullableTimestampTzArray(Unsafe.As<T, ReadOnlyMemory<PgTimestampTz?>?>(ref value));
        }
        if (oid == (uint)TypeOid.TimestampTzArray && typeof(T) == typeof(PgTimestampTz?[]))
        {
            return MpgsqlParameterValue.NullableTimestampTzArray(Unsafe.As<T, PgTimestampTz?[]>(ref value));
        }
        if (oid == (uint)TypeOid.Interval && typeof(T) == typeof(PgInterval))
        {
            return MpgsqlParameterValue.Interval(Unsafe.As<T, PgInterval>(ref value));
        }
        if (oid == (uint)TypeOid.Interval && typeof(T) == typeof(PgInterval?))
        {
            return MpgsqlParameterValue.Interval(Unsafe.As<T, PgInterval?>(ref value));
        }
        if (oid == (uint)TypeOid.IntervalArray && typeof(T) == typeof(ReadOnlyMemory<PgInterval>))
        {
            return MpgsqlParameterValue.IntervalArray(Unsafe.As<T, ReadOnlyMemory<PgInterval>>(ref value));
        }
        if (oid == (uint)TypeOid.IntervalArray && typeof(T) == typeof(ReadOnlyMemory<PgInterval>?))
        {
            return MpgsqlParameterValue.IntervalArray(Unsafe.As<T, ReadOnlyMemory<PgInterval>?>(ref value));
        }
        if (oid == (uint)TypeOid.IntervalArray && typeof(T) == typeof(PgInterval[]))
        {
            return MpgsqlParameterValue.IntervalArray(Unsafe.As<T, PgInterval[]>(ref value));
        }
        if (oid == (uint)TypeOid.IntervalArray && typeof(T) == typeof(ReadOnlyMemory<PgInterval?>))
        {
            return MpgsqlParameterValue.NullableIntervalArray(Unsafe.As<T, ReadOnlyMemory<PgInterval?>>(ref value));
        }
        if (oid == (uint)TypeOid.IntervalArray && typeof(T) == typeof(ReadOnlyMemory<PgInterval?>?))
        {
            return MpgsqlParameterValue.NullableIntervalArray(Unsafe.As<T, ReadOnlyMemory<PgInterval?>?>(ref value));
        }
        if (oid == (uint)TypeOid.IntervalArray && typeof(T) == typeof(PgInterval?[]))
        {
            return MpgsqlParameterValue.NullableIntervalArray(Unsafe.As<T, PgInterval?[]>(ref value));
        }
        if (oid == (uint)TypeOid.Inet && typeof(T) == typeof(PgInet))
        {
            return MpgsqlParameterValue.Inet(Unsafe.As<T, PgInet>(ref value));
        }
        if (oid == (uint)TypeOid.Inet && typeof(T) == typeof(PgInet?))
        {
            return MpgsqlParameterValue.Inet(Unsafe.As<T, PgInet?>(ref value));
        }
        if (oid == (uint)TypeOid.InetArray && typeof(T) == typeof(ReadOnlyMemory<PgInet>))
        {
            return MpgsqlParameterValue.InetArray(Unsafe.As<T, ReadOnlyMemory<PgInet>>(ref value));
        }
        if (oid == (uint)TypeOid.InetArray && typeof(T) == typeof(ReadOnlyMemory<PgInet>?))
        {
            return MpgsqlParameterValue.InetArray(Unsafe.As<T, ReadOnlyMemory<PgInet>?>(ref value));
        }
        if (oid == (uint)TypeOid.InetArray && typeof(T) == typeof(PgInet[]))
        {
            return MpgsqlParameterValue.InetArray(Unsafe.As<T, PgInet[]>(ref value));
        }
        if (oid == (uint)TypeOid.InetArray && typeof(T) == typeof(ReadOnlyMemory<PgInet?>))
        {
            return MpgsqlParameterValue.NullableInetArray(Unsafe.As<T, ReadOnlyMemory<PgInet?>>(ref value));
        }
        if (oid == (uint)TypeOid.InetArray && typeof(T) == typeof(ReadOnlyMemory<PgInet?>?))
        {
            return MpgsqlParameterValue.NullableInetArray(Unsafe.As<T, ReadOnlyMemory<PgInet?>?>(ref value));
        }
        if (oid == (uint)TypeOid.InetArray && typeof(T) == typeof(PgInet?[]))
        {
            return MpgsqlParameterValue.NullableInetArray(Unsafe.As<T, PgInet?[]>(ref value));
        }
        if (oid == (uint)TypeOid.Cidr && typeof(T) == typeof(PgInet))
        {
            return MpgsqlParameterValue.Cidr(Unsafe.As<T, PgInet>(ref value));
        }
        if (oid == (uint)TypeOid.Cidr && typeof(T) == typeof(PgInet?))
        {
            return MpgsqlParameterValue.Cidr(Unsafe.As<T, PgInet?>(ref value));
        }
        if (oid == (uint)TypeOid.CidrArray && typeof(T) == typeof(ReadOnlyMemory<PgInet>))
        {
            return MpgsqlParameterValue.CidrArray(Unsafe.As<T, ReadOnlyMemory<PgInet>>(ref value));
        }
        if (oid == (uint)TypeOid.CidrArray && typeof(T) == typeof(ReadOnlyMemory<PgInet>?))
        {
            return MpgsqlParameterValue.CidrArray(Unsafe.As<T, ReadOnlyMemory<PgInet>?>(ref value));
        }
        if (oid == (uint)TypeOid.CidrArray && typeof(T) == typeof(PgInet[]))
        {
            return MpgsqlParameterValue.CidrArray(Unsafe.As<T, PgInet[]>(ref value));
        }
        if (oid == (uint)TypeOid.CidrArray && typeof(T) == typeof(ReadOnlyMemory<PgInet?>))
        {
            return MpgsqlParameterValue.NullableCidrArray(Unsafe.As<T, ReadOnlyMemory<PgInet?>>(ref value));
        }
        if (oid == (uint)TypeOid.CidrArray && typeof(T) == typeof(ReadOnlyMemory<PgInet?>?))
        {
            return MpgsqlParameterValue.NullableCidrArray(Unsafe.As<T, ReadOnlyMemory<PgInet?>?>(ref value));
        }
        if (oid == (uint)TypeOid.CidrArray && typeof(T) == typeof(PgInet?[]))
        {
            return MpgsqlParameterValue.NullableCidrArray(Unsafe.As<T, PgInet?[]>(ref value));
        }
        if (oid == (uint)TypeOid.Oid && typeof(T) == typeof(uint))
        {
            return MpgsqlParameterValue.Oid(Unsafe.As<T, uint>(ref value));
        }
        if (oid == (uint)TypeOid.Oid && typeof(T) == typeof(uint?))
        {
            return MpgsqlParameterValue.Oid(Unsafe.As<T, uint?>(ref value));
        }
        if (oid == (uint)TypeOid.OidArray && typeof(T) == typeof(ReadOnlyMemory<uint>))
        {
            return MpgsqlParameterValue.OidArray(Unsafe.As<T, ReadOnlyMemory<uint>>(ref value));
        }
        if (oid == (uint)TypeOid.OidArray && typeof(T) == typeof(ReadOnlyMemory<uint>?))
        {
            return MpgsqlParameterValue.OidArray(Unsafe.As<T, ReadOnlyMemory<uint>?>(ref value));
        }
        if (oid == (uint)TypeOid.OidArray && typeof(T) == typeof(uint[]))
        {
            return MpgsqlParameterValue.OidArray(Unsafe.As<T, uint[]>(ref value));
        }
        if (oid == (uint)TypeOid.OidArray && typeof(T) == typeof(ReadOnlyMemory<uint?>))
        {
            return MpgsqlParameterValue.NullableOidArray(Unsafe.As<T, ReadOnlyMemory<uint?>>(ref value));
        }
        if (oid == (uint)TypeOid.OidArray && typeof(T) == typeof(ReadOnlyMemory<uint?>?))
        {
            return MpgsqlParameterValue.NullableOidArray(Unsafe.As<T, ReadOnlyMemory<uint?>?>(ref value));
        }
        if (oid == (uint)TypeOid.OidArray && typeof(T) == typeof(uint?[]))
        {
            return MpgsqlParameterValue.NullableOidArray(Unsafe.As<T, uint?[]>(ref value));
        }
        if (oid == (uint)TypeOid.Numeric && typeof(T) == typeof(decimal))
        {
            return MpgsqlParameterValue.Decimal(Unsafe.As<T, decimal>(ref value));
        }
        if (oid == (uint)TypeOid.Numeric && typeof(T) == typeof(decimal?))
        {
            return MpgsqlParameterValue.Decimal(Unsafe.As<T, decimal?>(ref value));
        }
        if (oid == (uint)TypeOid.NumericArray && typeof(T) == typeof(ReadOnlyMemory<decimal>))
        {
            return MpgsqlParameterValue.DecimalArray(Unsafe.As<T, ReadOnlyMemory<decimal>>(ref value));
        }
        if (oid == (uint)TypeOid.NumericArray && typeof(T) == typeof(ReadOnlyMemory<decimal>?))
        {
            return MpgsqlParameterValue.DecimalArray(Unsafe.As<T, ReadOnlyMemory<decimal>?>(ref value));
        }
        if (oid == (uint)TypeOid.NumericArray && typeof(T) == typeof(decimal[]))
        {
            return MpgsqlParameterValue.DecimalArray(Unsafe.As<T, decimal[]>(ref value));
        }
        if (oid == (uint)TypeOid.NumericArray && typeof(T) == typeof(ReadOnlyMemory<decimal?>))
        {
            return MpgsqlParameterValue.NullableDecimalArray(Unsafe.As<T, ReadOnlyMemory<decimal?>>(ref value));
        }
        if (oid == (uint)TypeOid.NumericArray && typeof(T) == typeof(ReadOnlyMemory<decimal?>?))
        {
            return MpgsqlParameterValue.NullableDecimalArray(Unsafe.As<T, ReadOnlyMemory<decimal?>?>(ref value));
        }
        if (oid == (uint)TypeOid.NumericArray && typeof(T) == typeof(decimal?[]))
        {
            return MpgsqlParameterValue.NullableDecimalArray(Unsafe.As<T, decimal?[]>(ref value));
        }
        if (oid == (uint)TypeOid.Date && typeof(T) == typeof(DateOnly))
        {
            return MpgsqlParameterValue.DateOnly(Unsafe.As<T, DateOnly>(ref value));
        }
        if (oid == (uint)TypeOid.Date && typeof(T) == typeof(DateOnly?))
        {
            return MpgsqlParameterValue.DateOnly(Unsafe.As<T, DateOnly?>(ref value));
        }
        if (oid == (uint)TypeOid.DateArray && typeof(T) == typeof(ReadOnlyMemory<DateOnly>))
        {
            return MpgsqlParameterValue.DateOnlyArray(Unsafe.As<T, ReadOnlyMemory<DateOnly>>(ref value));
        }
        if (oid == (uint)TypeOid.DateArray && typeof(T) == typeof(ReadOnlyMemory<DateOnly>?))
        {
            return MpgsqlParameterValue.DateOnlyArray(Unsafe.As<T, ReadOnlyMemory<DateOnly>?>(ref value));
        }
        if (oid == (uint)TypeOid.DateArray && typeof(T) == typeof(DateOnly[]))
        {
            return MpgsqlParameterValue.DateOnlyArray(Unsafe.As<T, DateOnly[]>(ref value));
        }
        if (oid == (uint)TypeOid.DateArray && typeof(T) == typeof(ReadOnlyMemory<DateOnly?>))
        {
            return MpgsqlParameterValue.NullableDateOnlyArray(Unsafe.As<T, ReadOnlyMemory<DateOnly?>>(ref value));
        }
        if (oid == (uint)TypeOid.DateArray && typeof(T) == typeof(ReadOnlyMemory<DateOnly?>?))
        {
            return MpgsqlParameterValue.NullableDateOnlyArray(Unsafe.As<T, ReadOnlyMemory<DateOnly?>?>(ref value));
        }
        if (oid == (uint)TypeOid.DateArray && typeof(T) == typeof(DateOnly?[]))
        {
            return MpgsqlParameterValue.NullableDateOnlyArray(Unsafe.As<T, DateOnly?[]>(ref value));
        }
        if (oid == (uint)TypeOid.Time && typeof(T) == typeof(TimeOnly))
        {
            return MpgsqlParameterValue.TimeOnly(Unsafe.As<T, TimeOnly>(ref value));
        }
        if (oid == (uint)TypeOid.Time && typeof(T) == typeof(TimeOnly?))
        {
            return MpgsqlParameterValue.TimeOnly(Unsafe.As<T, TimeOnly?>(ref value));
        }
        if (oid == (uint)TypeOid.TimeArray && typeof(T) == typeof(ReadOnlyMemory<TimeOnly>))
        {
            return MpgsqlParameterValue.TimeOnlyArray(Unsafe.As<T, ReadOnlyMemory<TimeOnly>>(ref value));
        }
        if (oid == (uint)TypeOid.TimeArray && typeof(T) == typeof(ReadOnlyMemory<TimeOnly>?))
        {
            return MpgsqlParameterValue.TimeOnlyArray(Unsafe.As<T, ReadOnlyMemory<TimeOnly>?>(ref value));
        }
        if (oid == (uint)TypeOid.TimeArray && typeof(T) == typeof(TimeOnly[]))
        {
            return MpgsqlParameterValue.TimeOnlyArray(Unsafe.As<T, TimeOnly[]>(ref value));
        }
        if (oid == (uint)TypeOid.TimeArray && typeof(T) == typeof(ReadOnlyMemory<TimeOnly?>))
        {
            return MpgsqlParameterValue.NullableTimeOnlyArray(Unsafe.As<T, ReadOnlyMemory<TimeOnly?>>(ref value));
        }
        if (oid == (uint)TypeOid.TimeArray && typeof(T) == typeof(ReadOnlyMemory<TimeOnly?>?))
        {
            return MpgsqlParameterValue.NullableTimeOnlyArray(Unsafe.As<T, ReadOnlyMemory<TimeOnly?>?>(ref value));
        }
        if (oid == (uint)TypeOid.TimeArray && typeof(T) == typeof(TimeOnly?[]))
        {
            return MpgsqlParameterValue.NullableTimeOnlyArray(Unsafe.As<T, TimeOnly?[]>(ref value));
        }
        if (oid == (uint)TypeOid.Timestamp && typeof(T) == typeof(DateTime))
        {
            return MpgsqlParameterValue.DateTime(Unsafe.As<T, DateTime>(ref value));
        }
        if (oid == (uint)TypeOid.Timestamp && typeof(T) == typeof(DateTime?))
        {
            return MpgsqlParameterValue.DateTime(Unsafe.As<T, DateTime?>(ref value));
        }
        if (oid == (uint)TypeOid.TimestampArray && typeof(T) == typeof(ReadOnlyMemory<DateTime>))
        {
            return MpgsqlParameterValue.DateTimeArray(Unsafe.As<T, ReadOnlyMemory<DateTime>>(ref value));
        }
        if (oid == (uint)TypeOid.TimestampArray && typeof(T) == typeof(ReadOnlyMemory<DateTime>?))
        {
            return MpgsqlParameterValue.DateTimeArray(Unsafe.As<T, ReadOnlyMemory<DateTime>?>(ref value));
        }
        if (oid == (uint)TypeOid.TimestampArray && typeof(T) == typeof(DateTime[]))
        {
            return MpgsqlParameterValue.DateTimeArray(Unsafe.As<T, DateTime[]>(ref value));
        }
        if (oid == (uint)TypeOid.TimestampArray && typeof(T) == typeof(ReadOnlyMemory<DateTime?>))
        {
            return MpgsqlParameterValue.NullableDateTimeArray(Unsafe.As<T, ReadOnlyMemory<DateTime?>>(ref value));
        }
        if (oid == (uint)TypeOid.TimestampArray && typeof(T) == typeof(ReadOnlyMemory<DateTime?>?))
        {
            return MpgsqlParameterValue.NullableDateTimeArray(Unsafe.As<T, ReadOnlyMemory<DateTime?>?>(ref value));
        }
        if (oid == (uint)TypeOid.TimestampArray && typeof(T) == typeof(DateTime?[]))
        {
            return MpgsqlParameterValue.NullableDateTimeArray(Unsafe.As<T, DateTime?[]>(ref value));
        }
        if (oid == (uint)TypeOid.TimestampTz && typeof(T) == typeof(DateTimeOffset))
        {
            return MpgsqlParameterValue.DateTimeOffset(Unsafe.As<T, DateTimeOffset>(ref value));
        }
        if (oid == (uint)TypeOid.TimestampTz && typeof(T) == typeof(DateTimeOffset?))
        {
            return MpgsqlParameterValue.DateTimeOffset(Unsafe.As<T, DateTimeOffset?>(ref value));
        }
        if (oid == (uint)TypeOid.TimestampTzArray && typeof(T) == typeof(ReadOnlyMemory<DateTimeOffset>))
        {
            return MpgsqlParameterValue.DateTimeOffsetArray(Unsafe.As<T, ReadOnlyMemory<DateTimeOffset>>(ref value));
        }
        if (oid == (uint)TypeOid.TimestampTzArray && typeof(T) == typeof(ReadOnlyMemory<DateTimeOffset>?))
        {
            return MpgsqlParameterValue.DateTimeOffsetArray(Unsafe.As<T, ReadOnlyMemory<DateTimeOffset>?>(ref value));
        }
        if (oid == (uint)TypeOid.TimestampTzArray && typeof(T) == typeof(DateTimeOffset[]))
        {
            return MpgsqlParameterValue.DateTimeOffsetArray(Unsafe.As<T, DateTimeOffset[]>(ref value));
        }
        if (oid == (uint)TypeOid.TimestampTzArray && typeof(T) == typeof(ReadOnlyMemory<DateTimeOffset?>))
        {
            return MpgsqlParameterValue.NullableDateTimeOffsetArray(Unsafe.As<T, ReadOnlyMemory<DateTimeOffset?>>(ref value));
        }
        if (oid == (uint)TypeOid.TimestampTzArray && typeof(T) == typeof(ReadOnlyMemory<DateTimeOffset?>?))
        {
            return MpgsqlParameterValue.NullableDateTimeOffsetArray(Unsafe.As<T, ReadOnlyMemory<DateTimeOffset?>?>(ref value));
        }
        if (oid == (uint)TypeOid.TimestampTzArray && typeof(T) == typeof(DateTimeOffset?[]))
        {
            return MpgsqlParameterValue.NullableDateTimeOffsetArray(Unsafe.As<T, DateTimeOffset?[]>(ref value));
        }
        if (oid == (uint)TypeOid.Interval && typeof(T) == typeof(TimeSpan))
        {
            return MpgsqlParameterValue.TimeSpan(Unsafe.As<T, TimeSpan>(ref value));
        }
        if (oid == (uint)TypeOid.Interval && typeof(T) == typeof(TimeSpan?))
        {
            return MpgsqlParameterValue.TimeSpan(Unsafe.As<T, TimeSpan?>(ref value));
        }
        if (oid == (uint)TypeOid.IntervalArray && typeof(T) == typeof(ReadOnlyMemory<TimeSpan>))
        {
            return MpgsqlParameterValue.TimeSpanArray(Unsafe.As<T, ReadOnlyMemory<TimeSpan>>(ref value));
        }
        if (oid == (uint)TypeOid.IntervalArray && typeof(T) == typeof(ReadOnlyMemory<TimeSpan>?))
        {
            return MpgsqlParameterValue.TimeSpanArray(Unsafe.As<T, ReadOnlyMemory<TimeSpan>?>(ref value));
        }
        if (oid == (uint)TypeOid.IntervalArray && typeof(T) == typeof(TimeSpan[]))
        {
            return MpgsqlParameterValue.TimeSpanArray(Unsafe.As<T, TimeSpan[]>(ref value));
        }
        if (oid == (uint)TypeOid.IntervalArray && typeof(T) == typeof(ReadOnlyMemory<TimeSpan?>))
        {
            return MpgsqlParameterValue.NullableTimeSpanArray(Unsafe.As<T, ReadOnlyMemory<TimeSpan?>>(ref value));
        }
        if (oid == (uint)TypeOid.IntervalArray && typeof(T) == typeof(ReadOnlyMemory<TimeSpan?>?))
        {
            return MpgsqlParameterValue.NullableTimeSpanArray(Unsafe.As<T, ReadOnlyMemory<TimeSpan?>?>(ref value));
        }
        if (oid == (uint)TypeOid.IntervalArray && typeof(T) == typeof(TimeSpan?[]))
        {
            return MpgsqlParameterValue.NullableTimeSpanArray(Unsafe.As<T, TimeSpan?[]>(ref value));
        }
        if (oid == (uint)TypeOid.ByteaArray && typeof(T) == typeof(ReadOnlyMemory<byte[]?>))
        {
            return MpgsqlParameterValue.ByteaByteArrays(Unsafe.As<T, ReadOnlyMemory<byte[]?>>(ref value));
        }
        if (oid == (uint)TypeOid.ByteaArray && typeof(T) == typeof(ReadOnlyMemory<byte[]?>?))
        {
            return MpgsqlParameterValue.ByteaByteArrays(Unsafe.As<T, ReadOnlyMemory<byte[]?>?>(ref value));
        }
        if (oid == (uint)TypeOid.ByteaArray && typeof(T) == typeof(byte[]?[]))
        {
            return MpgsqlParameterValue.ByteaByteArrays(Unsafe.As<T, byte[]?[]>(ref value));
        }
        throw new InvalidCastException($"Parameter {typeof(T)} cannot be encoded as PostgreSQL OID {oid}.");
    }
}