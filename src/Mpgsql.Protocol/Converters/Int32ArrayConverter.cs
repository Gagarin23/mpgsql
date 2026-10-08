using System.Buffers;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Int32[] for ReadOnlyMemory&lt;int&gt;.</summary>
/// <remarks>
///     Supports empty and one-dimensional arrays; lower bounds are normalized to zero-based memory.
///     Writes only payload, with big-endian headers and element lengths. Capacity/overlap are checked
///     before writing. Read returns owned storage, or fills reusable storage without a payload copy.
///     An invalid element may leave reusable output partially written. SQL NULL is an outer field marker.
/// </remarks>
public static class Int32ArrayConverter
{
    public const uint ElementTypeOid = (uint)TypeOid.Int32;
    public const uint ArrayTypeOid = (uint)TypeOid.Int32Array;
    public static int GetByteCount(ReadOnlyMemory<int> value)
    {
        return BinaryArray<int, Int32Codec>.Measure(value.Span);
    }
    public static int GetByteCount(int elementCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elementCount);
        return ArrayPayload.Measure(elementCount, checked(elementCount * 4));
    }
    public static int Write(ReadOnlyMemory<int> value, Span<byte> destination)
    {
        return BinaryArray<int, Int32Codec>.Write(value, destination);
    }
    public static void Write(ReadOnlyMemory<int> value, IBufferWriter<byte> destination)
    {
        BinaryArray<int, Int32Codec>.Write(value, destination);
    }
    public static ReadOnlyMemory<int> Read(ReadOnlySpan<byte> payload)
    {
        return BinaryArray<int, Int32Codec>.Read(payload);
    }
    public static ReadOnlyMemory<int> Read(ReadOnlySequence<byte> payload)
    {
        return BinaryArray<int, Int32Codec>.Read(payload);
    }
    public static int Read(ReadOnlySpan<byte> payload, Span<int> destination)
    {
        return BinaryArray<int, Int32Codec>.Read(payload, destination);
    }
    public static int Read(ReadOnlySequence<byte> payload, Span<int> destination)
    {
        return BinaryArray<int, Int32Codec>.Read(payload, destination);
    }
}