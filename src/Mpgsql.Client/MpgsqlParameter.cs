using Mpgsql.Converters;
using Mpgsql.Internal;

namespace Mpgsql;

/// <summary>A positional binary parameter. The low-level sender borrows input memory until SendQueryAsync
/// finishes; commands and batches borrow it for their entire execution.</summary>
public readonly partial struct MpgsqlParameter
{
    private readonly byte _kind;
    private readonly bool _isNull;
    private readonly long _number;
    private readonly ReadOnlyMemory<long> _array;
    private readonly ReadOnlyMemory<long?> _nullableArray;
    private readonly uint _oid;
    private readonly IParameterValue? _value;

    private MpgsqlParameter(byte kind,
        bool isNull,
        long number = 0,
        ReadOnlyMemory<long> array = default,
        ReadOnlyMemory<long?> nullableArray = default,
        uint oid = 0,
        IParameterValue? value = null)
        => (_kind, _isNull, _number, _array, _nullableArray, _oid, _value)
            = (kind, isNull, number, array, nullableArray, oid, value);

    public static MpgsqlParameter Int64(long? value) => new(1,
        !value.HasValue,
        value.GetValueOrDefault());
    public static MpgsqlParameter Int64Array(ReadOnlyMemory<long>? value)
        => new(2,
            !value.HasValue,
            array: value.GetValueOrDefault());
    public static MpgsqlParameter NullableInt64Array(ReadOnlyMemory<long?>? value)
        => new(3,
            !value.HasValue,
            nullableArray: value.GetValueOrDefault());

    internal uint PostgresTypeOid => _kind switch
    {
        1      => Int64Converter.TypeOid,
        2 or 3 => Int64ArrayConverter.ArrayTypeOid,
        4 => _oid,
        _      => throw new InvalidOperationException("The MpgsqlParameter is not initialized.")
    };

    internal int PayloadLength
    {
        get
        {
            _ = PostgresTypeOid;
            if (_isNull)
            {
                return -1;
            }
            return _kind switch
            {
                1 => Int64Converter.ByteCount,
                2 => Int64ArrayConverter.GetByteCount(_array),
                3 => NullableInt64ArrayConverter.GetByteCount(_nullableArray),
                _ => _value!.Length
            };
        }
    }

    internal bool IsNull => _isNull;

    internal int WritePayload(Span<byte> destination)
    {
        switch (_kind)
        {
            case 1:
                return Int64Converter.Write(_number,
                    destination);
            case 2:
                return Int64ArrayConverter.Write(_array,
                    destination);
            case 3:
                return NullableInt64ArrayConverter.Write(_nullableArray,
                    destination);
            case 4:
                return _value!.Write(destination);
            default: throw new InvalidOperationException("The MpgsqlParameter is not initialized.");
        }
    }

    private static MpgsqlParameter Scalar<T, TCodec>(uint oid, T? value)
        where T : struct where TCodec : struct, IBinaryCodec<T>
        => new(4, !value.HasValue, oid: oid,
            value: value.HasValue ? new ScalarParameterValue<T, TCodec>(value.Value) : null);

    private static MpgsqlParameter Reference<T, TCodec>(uint oid, T? value)
        where T : class where TCodec : struct, IBinaryCodec<T>
        => new(4, value is null, oid: oid,
            value: value is null ? null : new ScalarParameterValue<T, TCodec>(value));

    private static MpgsqlParameter Array<T, TCodec>(uint oid, ReadOnlyMemory<T>? value)
        where TCodec : struct, IBinaryCodec<T>
        => new(4, !value.HasValue, oid: oid,
            value: value.HasValue ? new ArrayParameterValue<T, TCodec>(value.Value) : null);

    private static MpgsqlParameter NullableArray<T, TCodec>(uint oid, ReadOnlyMemory<T?>? value)
        where T : struct where TCodec : struct, IBinaryCodec<T>
        => new(4, !value.HasValue, oid: oid,
            value: value.HasValue ? new NullableArrayParameterValue<T, TCodec>(value.Value) : null);

    private static MpgsqlParameter ReferenceArray<T, TCodec>(uint oid, ReadOnlyMemory<T?>? value)
        where T : class where TCodec : struct, IBinaryCodec<T>
        => new(4, !value.HasValue, oid: oid,
            value: value.HasValue ? new ReferenceArrayParameterValue<T, TCodec>(value.Value) : null);
}
