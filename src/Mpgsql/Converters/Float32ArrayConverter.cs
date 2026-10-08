using System.Buffers;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Float32[] for ReadOnlyMemory&lt;float&gt;.</summary>
/// <remarks>
///     Supports empty and one-dimensional arrays; lower bounds are normalized to zero-based memory.
///     Writes only payload, with big-endian headers and element lengths. Capacity/overlap are checked
///     before writing. Read returns owned storage, or fills reusable storage without a payload copy.
///     An invalid element may leave reusable output partially written. SQL NULL is an outer field marker.
/// </remarks>
public static class Float32ArrayConverter
{
    public const uint ElementTypeOid = (uint)TypeOid.Float32;
    public const uint ArrayTypeOid = (uint)TypeOid.Float32Array;
    public static int GetByteCount(ReadOnlyMemory<float> value)
    {
        return BinaryArray<float, Float32Codec>.Measure(value.Span);
    }
    public static int GetByteCount(int elementCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elementCount);
        return ArrayPayload.Measure(elementCount, checked(elementCount * 4));
    }
    public static int Write(ReadOnlyMemory<float> value, Span<byte> destination)
    {
        return BinaryArray<float, Float32Codec>.Write(value, destination);
    }
    public static void Write(ReadOnlyMemory<float> value, IBufferWriter<byte> destination)
    {
        BinaryArray<float, Float32Codec>.Write(value, destination);
    }
    public static ReadOnlyMemory<float> Read(ReadOnlySpan<byte> payload)
    {
        return BinaryArray<float, Float32Codec>.Read(payload);
    }
    public static ReadOnlyMemory<float> Read(ReadOnlySequence<byte> payload)
    {
        return BinaryArray<float, Float32Codec>.Read(payload);
    }
    public static int Read(ReadOnlySpan<byte> payload, Span<float> destination)
    {
        return BinaryArray<float, Float32Codec>.Read(payload, destination);
    }
    public static int Read(ReadOnlySequence<byte> payload, Span<float> destination)
    {
        return BinaryArray<float, Float32Codec>.Read(payload, destination);
    }
}