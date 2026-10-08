using System.Buffers;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Float64[] with NULL elements for ReadOnlyMemory&lt;double?&gt;.</summary>
/// <remarks>Same framing/buffer contract as Float64ArrayConverter; NULL elements have length -1 and no payload.</remarks>
public static class NullableFloat64ArrayConverter
{
    public const uint ElementTypeOid = (uint)TypeOid.Float64;
    public const uint ArrayTypeOid = (uint)TypeOid.Float64Array;
    public static int GetByteCount(ReadOnlyMemory<double?> value)
    {
        return BinaryNullableArray<double, Float64Codec>.Measure(value.Span, out _);
    }
    public static int GetByteCount(int elementCount, int nullCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elementCount);
        ArgumentOutOfRangeException.ThrowIfNegative(nullCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(nullCount, elementCount);
        return ArrayPayload.Measure(elementCount, checked((elementCount - nullCount) * 8));
    }
    public static int Write(ReadOnlyMemory<double?> value, Span<byte> destination)
    {
        return BinaryNullableArray<double, Float64Codec>.Write(value, destination);
    }
    public static void Write(ReadOnlyMemory<double?> value, IBufferWriter<byte> destination)
    {
        BinaryNullableArray<double, Float64Codec>.Write(value, destination);
    }
    public static ReadOnlyMemory<double?> Read(ReadOnlySpan<byte> payload)
    {
        return BinaryNullableArray<double, Float64Codec>.Read(payload);
    }
    public static ReadOnlyMemory<double?> Read(ReadOnlySequence<byte> payload)
    {
        return BinaryNullableArray<double, Float64Codec>.Read(payload);
    }
    public static int Read(ReadOnlySpan<byte> payload, Span<double?> destination)
    {
        return BinaryNullableArray<double, Float64Codec>.Read(payload, destination);
    }
    public static int Read(ReadOnlySequence<byte> payload, Span<double?> destination)
    {
        return BinaryNullableArray<double, Float64Codec>.Read(payload, destination);
    }
}