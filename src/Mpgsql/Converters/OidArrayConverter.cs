using System.Buffers;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Oid[] for ReadOnlyMemory&lt;uint&gt;.</summary>
/// <remarks>
///     Supports empty and one-dimensional arrays; lower bounds are normalized to zero-based memory.
///     Writes only payload, with big-endian headers and element lengths. Capacity/overlap are checked
///     before writing. Read returns owned storage, or fills reusable storage without a payload copy.
///     An invalid element may leave reusable output partially written. SQL NULL is an outer field marker.
/// </remarks>
public static class OidArrayConverter
{
    public const uint ElementTypeOid = (uint)TypeOid.Oid;
    public const uint ArrayTypeOid = (uint)TypeOid.OidArray;
    public static int GetByteCount(ReadOnlyMemory<uint> value)
    {
        return BinaryArray<uint, OidCodec>.Measure(value.Span);
    }
    public static int GetByteCount(int elementCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elementCount);
        return ArrayPayload.Measure(elementCount, checked(elementCount * 4));
    }
    public static int Write(ReadOnlyMemory<uint> value, Span<byte> destination)
    {
        return BinaryArray<uint, OidCodec>.Write(value, destination);
    }
    public static void Write(ReadOnlyMemory<uint> value, IBufferWriter<byte> destination)
    {
        BinaryArray<uint, OidCodec>.Write(value, destination);
    }
    public static ReadOnlyMemory<uint> Read(ReadOnlySpan<byte> payload)
    {
        return BinaryArray<uint, OidCodec>.Read(payload);
    }
    public static ReadOnlyMemory<uint> Read(ReadOnlySequence<byte> payload)
    {
        return BinaryArray<uint, OidCodec>.Read(payload);
    }
    public static int Read(ReadOnlySpan<byte> payload, Span<uint> destination)
    {
        return BinaryArray<uint, OidCodec>.Read(payload, destination);
    }
    public static int Read(ReadOnlySequence<byte> payload, Span<uint> destination)
    {
        return BinaryArray<uint, OidCodec>.Read(payload, destination);
    }
}