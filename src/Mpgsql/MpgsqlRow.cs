using System.Buffers;
using System.Runtime.CompilerServices;
using Mpgsql.Internal;
using Mpgsql.Protocol;

namespace Mpgsql;

/// <summary>A view of the current row, valid only during a row mapper call.</summary>
public readonly ref struct MpgsqlRow
{
    private readonly AdoCursor _cursor;
    private readonly MpgsqlTypeMapper? _mapper;

    internal MpgsqlRow(AdoCursor cursor, MpgsqlTypeMapper? mapper)
    {
        _cursor = cursor;
        _mapper = mapper;
    }

    public int FieldCount => _cursor.Columns.Length;
    public ReadOnlyMemory<RowField> Columns => _cursor.Columns;
    public bool IsDBNull(int ordinal) => _cursor.IsDBNull(ordinal);

    /// <summary>Borrowed bytes must be consumed before the mapper returns.</summary>
    public ReadOnlySequence<byte>? GetRawValue(int ordinal) => _cursor.GetRawValue(ordinal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T GetFieldValue<T>(int ordinal)
    {
        if (typeof(T) == typeof(long) && _cursor.TryGetBorrowedBigint(ordinal, out var bigint))
            return (T)(object)(bigint ?? throw new InvalidCastException("The value is SQL NULL."));
        return ReadSlow<T>(ordinal);
    }

    private T ReadSlow<T>(int ordinal)
    {
        if (typeof(T) == typeof(object))
        {
            var payload = _cursor.GetRawValue(ordinal);
            return (T)(payload is { } bytes ? ResultValue.Read(_cursor.Columns.Span[ordinal].DataTypeOid, bytes) : DBNull.Value);
        }
        try { return _cursor.GetFieldValue<T>(ordinal, _mapper); }
        catch (InvalidOperationException) when (_cursor.IsDBNull(ordinal))
        {
            throw new InvalidCastException("The value is SQL NULL.");
        }
    }
}
