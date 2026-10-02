using Mpgsql.Converters;

namespace Mpgsql;

/// <summary>A positional, binary parameter. Array memory is borrowed until SendQueryAsync finishes.</summary>
public readonly struct MpgsqlParameter
{
    private readonly byte _kind;
    private readonly bool _isNull;
    private readonly long _number;
    private readonly ReadOnlyMemory<long> _array;
    private readonly ReadOnlyMemory<long?> _nullableArray;

    private MpgsqlParameter(byte kind,
        bool isNull,
        long number = 0,
        ReadOnlyMemory<long> array = default,
        ReadOnlyMemory<long?> nullableArray = default)
        => (_kind, _isNull, _number, _array, _nullableArray) = (kind, isNull, number, array, nullableArray);

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

    internal uint Oid => _kind switch
    {
        1      => Int64Converter.TypeOid,
        2 or 3 => Int64ArrayConverter.ArrayTypeOid,
        _      => throw new InvalidOperationException("The MpgsqlParameter is not initialized.")
    };

    internal int PayloadLength
    {
        get
        {
            _ = Oid;
            if (_isNull)
            {
                return -1;
            }
            return _kind switch
            {
                1 => Int64Converter.ByteCount,
                2 => Int64ArrayConverter.GetByteCount(_array),
                _ => NullableInt64ArrayConverter.GetByteCount(_nullableArray)
            };
        }
    }

    internal void WritePayload(Span<byte> destination)
    {
        switch (_kind)
        {
            case 1:
                Int64Converter.Write(_number,
                    destination); break;
            case 2:
                Int64ArrayConverter.Write(_array,
                    destination); break;
            case 3:
                NullableInt64ArrayConverter.Write(_nullableArray,
                    destination); break;
            default: throw new InvalidOperationException("The MpgsqlParameter is not initialized.");
        }
    }
}