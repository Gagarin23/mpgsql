using System.Buffers;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Float32[] with NULL elements for ReadOnlyMemory&lt;float?&gt;.</summary>
/// <remarks>Same framing/buffer contract as Float32ArrayConverter; NULL elements have length -1 and no payload.</remarks>
public static class NullableFloat32ArrayConverter
{
    public const uint ElementTypeOid = (uint)TypeOid.Float32;
    public const uint ArrayTypeOid = (uint)TypeOid.Float32Array;
    public static int GetByteCount(ReadOnlyMemory<float?> value)
    {
        return BinaryNullableArray<float, Float32Codec>.Measure(value.Span, out _);
    }
    public static int GetByteCount(int elementCount, int nullCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elementCount);
        ArgumentOutOfRangeException.ThrowIfNegative(nullCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(nullCount, elementCount);
        return ArrayPayload.Measure(elementCount, checked((elementCount - nullCount) * 4));
    }
    public static int Write(ReadOnlyMemory<float?> value, Span<byte> destination)
    {
        return BinaryNullableArray<float, Float32Codec>.Write(value, destination);
    }
    public static void Write(ReadOnlyMemory<float?> value, IBufferWriter<byte> destination)
    {
        BinaryNullableArray<float, Float32Codec>.Write(value, destination);
    }
    public static ReadOnlyMemory<float?> Read(ReadOnlySpan<byte> payload)
    {
        return BinaryNullableArray<float, Float32Codec>.Read(payload);
    }
    public static ReadOnlyMemory<float?> Read(ReadOnlySequence<byte> payload)
    {
        return BinaryNullableArray<float, Float32Codec>.Read(payload);
    }
    public static int Read(ReadOnlySpan<byte> payload, Span<float?> destination)
    {
        return BinaryNullableArray<float, Float32Codec>.Read(payload, destination);
    }
    public static int Read(ReadOnlySequence<byte> payload, Span<float?> destination)
    {
        return BinaryNullableArray<float, Float32Codec>.Read(payload, destination);
    }
}