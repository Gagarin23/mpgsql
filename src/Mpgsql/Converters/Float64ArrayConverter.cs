using System.Buffers;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Float64[] for ReadOnlyMemory&lt;double&gt;.</summary>
/// <remarks>
///     Supports empty and one-dimensional arrays; lower bounds are normalized to zero-based memory.
///     Writes only payload, with big-endian headers and element lengths. Capacity/overlap are checked
///     before writing. Read returns owned storage, or fills reusable storage without a payload copy.
///     An invalid element may leave reusable output partially written. SQL NULL is an outer field marker.
/// </remarks>
public static class Float64ArrayConverter
{
    public const uint ElementTypeOid = (uint)TypeOid.Float64;
    public const uint ArrayTypeOid = (uint)TypeOid.Float64Array;
    public static int GetByteCount(ReadOnlyMemory<double> value)
    {
        return BinaryArray<double, Float64Codec>.Measure(value.Span);
    }
    public static int GetByteCount(int elementCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elementCount);
        return ArrayPayload.Measure(elementCount, checked(elementCount * 8));
    }
    public static int Write(ReadOnlyMemory<double> value, Span<byte> destination)
    {
        return BinaryArray<double, Float64Codec>.Write(value, destination);
    }
    public static void Write(ReadOnlyMemory<double> value, IBufferWriter<byte> destination)
    {
        BinaryArray<double, Float64Codec>.Write(value, destination);
    }
    public static ReadOnlyMemory<double> Read(ReadOnlySpan<byte> payload)
    {
        return BinaryArray<double, Float64Codec>.Read(payload);
    }
    public static ReadOnlyMemory<double> Read(ReadOnlySequence<byte> payload)
    {
        return BinaryArray<double, Float64Codec>.Read(payload);
    }
    public static int Read(ReadOnlySpan<byte> payload, Span<double> destination)
    {
        return BinaryArray<double, Float64Codec>.Read(payload, destination);
    }
    public static int Read(ReadOnlySequence<byte> payload, Span<double> destination)
    {
        return BinaryArray<double, Float64Codec>.Read(payload, destination);
    }
}