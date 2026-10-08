using System.Buffers;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Int32[] with NULL elements for ReadOnlyMemory&lt;int?&gt;.</summary>
/// <remarks>Same framing/buffer contract as Int32ArrayConverter; NULL elements have length -1 and no payload.</remarks>
public static class NullableInt32ArrayConverter
{
    public const uint ElementTypeOid = (uint)TypeOid.Int32;
    public const uint ArrayTypeOid = (uint)TypeOid.Int32Array;
    public static int GetByteCount(ReadOnlyMemory<int?> value)
    {
        return BinaryNullableArray<int, Int32Codec>.Measure(value.Span, out _);
    }
    public static int GetByteCount(int elementCount, int nullCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elementCount);
        ArgumentOutOfRangeException.ThrowIfNegative(nullCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(nullCount, elementCount);
        return ArrayPayload.Measure(elementCount, checked((elementCount - nullCount) * 4));
    }
    public static int Write(ReadOnlyMemory<int?> value, Span<byte> destination)
    {
        return BinaryNullableArray<int, Int32Codec>.Write(value, destination);
    }
    public static void Write(ReadOnlyMemory<int?> value, IBufferWriter<byte> destination)
    {
        BinaryNullableArray<int, Int32Codec>.Write(value, destination);
    }
    public static ReadOnlyMemory<int?> Read(ReadOnlySpan<byte> payload)
    {
        return BinaryNullableArray<int, Int32Codec>.Read(payload);
    }
    public static ReadOnlyMemory<int?> Read(ReadOnlySequence<byte> payload)
    {
        return BinaryNullableArray<int, Int32Codec>.Read(payload);
    }
    public static int Read(ReadOnlySpan<byte> payload, Span<int?> destination)
    {
        return BinaryNullableArray<int, Int32Codec>.Read(payload, destination);
    }
    public static int Read(ReadOnlySequence<byte> payload, Span<int?> destination)
    {
        return BinaryNullableArray<int, Int32Codec>.Read(payload, destination);
    }
}