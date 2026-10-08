using System.Buffers;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Money[] for ReadOnlyMemory&lt;long&gt;.</summary>
/// <remarks>
///     Supports empty and one-dimensional arrays; lower bounds are normalized to zero-based memory.
///     Writes only payload, with big-endian headers and element lengths. Capacity/overlap are checked
///     before writing. Read returns owned storage, or fills reusable storage without a payload copy.
///     An invalid element may leave reusable output partially written. SQL NULL is an outer field marker.
/// </remarks>
public static class MoneyArrayConverter
{
    public const uint ElementTypeOid = (uint)TypeOid.Money;
    public const uint ArrayTypeOid = (uint)TypeOid.MoneyArray;
    public static int GetByteCount(ReadOnlyMemory<long> value)
    {
        return BinaryArray<long, MoneyCodec>.Measure(value.Span);
    }
    public static int GetByteCount(int elementCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elementCount);
        return ArrayPayload.Measure(elementCount, checked(elementCount * 8));
    }
    public static int Write(ReadOnlyMemory<long> value, Span<byte> destination)
    {
        return BinaryArray<long, MoneyCodec>.Write(value, destination);
    }
    public static void Write(ReadOnlyMemory<long> value, IBufferWriter<byte> destination)
    {
        BinaryArray<long, MoneyCodec>.Write(value, destination);
    }
    public static ReadOnlyMemory<long> Read(ReadOnlySpan<byte> payload)
    {
        return BinaryArray<long, MoneyCodec>.Read(payload);
    }
    public static ReadOnlyMemory<long> Read(ReadOnlySequence<byte> payload)
    {
        return BinaryArray<long, MoneyCodec>.Read(payload);
    }
    public static int Read(ReadOnlySpan<byte> payload, Span<long> destination)
    {
        return BinaryArray<long, MoneyCodec>.Read(payload, destination);
    }
    public static int Read(ReadOnlySequence<byte> payload, Span<long> destination)
    {
        return BinaryArray<long, MoneyCodec>.Read(payload, destination);
    }
}