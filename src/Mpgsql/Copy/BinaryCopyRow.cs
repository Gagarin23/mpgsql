using System.Buffers;
using System.Buffers.Binary;
using Mpgsql.Converters;

namespace Mpgsql.Copy;

/// <summary>A COPY tuple borrowing input bytes and caller-owned field storage.</summary>
public readonly struct BinaryCopyRow
{
    private readonly ReadOnlyMemory<ReadOnlySequence<byte>?> _fields;
    internal BinaryCopyRow(ReadOnlyMemory<ReadOnlySequence<byte>?> fields) => _fields = fields;
    public int Count => _fields.Length;
    public ReadOnlySequence<byte>? this[int index] => _fields.Span[index];
    public bool IsNull(int index) => !this[index].HasValue;

    public long ReadInt64(int index)
    {
        var value = RequireValue(index);
        if (value.Length != 8)
        {
            throw new InvalidDataException("A binary bigint requires exactly 8 bytes.");
        }
        if (value.IsSingleSegment)
        {
            return BinaryPrimitives.ReadInt64BigEndian(value.FirstSpan);
        }
        var reader = new SequenceReader<byte>(value);
        reader.TryReadBigEndian(out long result);
        return result;
    }

    public long? ReadNullableInt64(int index) => Int64Converter.ReadNullable(this[index]);

    public ReadOnlyMemory<long> ReadLongArray(int index) => Int64ArrayConverter.Read(RequireValue(index));
    public int ReadLongArray(int index,
        Span<long> destination) => Int64ArrayConverter.Read(RequireValue(index),
        destination);

    public ReadOnlyMemory<long?> ReadNullableLongArray(int index) => NullableInt64ArrayConverter.Read(RequireValue(index));
    public int ReadNullableLongArray(int index,
        Span<long?> destination) => NullableInt64ArrayConverter.Read(RequireValue(index),
        destination);

    private ReadOnlySequence<byte> RequireValue(int index) => this[index]
                                                              ?? throw new InvalidOperationException("The COPY field is SQL NULL.");
}