using System.Buffers;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Money[] with NULL elements for ReadOnlyMemory&lt;long?&gt;.</summary>
/// <remarks>Same framing/buffer contract as MoneyArrayConverter; NULL elements have length -1 and no payload.</remarks>
public static class NullableMoneyArrayConverter
{
    public const uint ElementTypeOid = (uint)TypeOid.Money;
    public const uint ArrayTypeOid = (uint)TypeOid.MoneyArray;
    public static int GetByteCount(ReadOnlyMemory<long?> value)
    {
        return BinaryNullableArray<long, MoneyCodec>.Measure(value.Span, out _);
    }
    public static int GetByteCount(int elementCount, int nullCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elementCount);
        ArgumentOutOfRangeException.ThrowIfNegative(nullCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(nullCount, elementCount);
        return ArrayPayload.Measure(elementCount, checked((elementCount - nullCount) * 8));
    }
    public static int Write(ReadOnlyMemory<long?> value, Span<byte> destination)
    {
        return BinaryNullableArray<long, MoneyCodec>.Write(value, destination);
    }
    public static void Write(ReadOnlyMemory<long?> value, IBufferWriter<byte> destination)
    {
        BinaryNullableArray<long, MoneyCodec>.Write(value, destination);
    }
    public static ReadOnlyMemory<long?> Read(ReadOnlySpan<byte> payload)
    {
        return BinaryNullableArray<long, MoneyCodec>.Read(payload);
    }
    public static ReadOnlyMemory<long?> Read(ReadOnlySequence<byte> payload)
    {
        return BinaryNullableArray<long, MoneyCodec>.Read(payload);
    }
    public static int Read(ReadOnlySpan<byte> payload, Span<long?> destination)
    {
        return BinaryNullableArray<long, MoneyCodec>.Read(payload, destination);
    }
    public static int Read(ReadOnlySequence<byte> payload, Span<long?> destination)
    {
        return BinaryNullableArray<long, MoneyCodec>.Read(payload, destination);
    }
}