using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL TimeTz[] for ReadOnlyMemory&lt;PgTimeTz&gt;.</summary>
/// <remarks>
///     Supports empty and one-dimensional arrays; lower bounds are normalized to zero-based memory.
///     Writes only payload, with big-endian headers and element lengths. Capacity/overlap are checked
///     before writing. Read returns owned storage, or fills reusable storage without a payload copy.
///     An invalid element may leave reusable output partially written. SQL NULL is an outer field marker.
/// </remarks>
public static class TimeTzArrayConverter
{
    public const uint ElementTypeOid = (uint)TypeOid.TimeTz;
    public const uint ArrayTypeOid = (uint)TypeOid.TimeTzArray;
    public static int GetByteCount(ReadOnlyMemory<PgTimeTz> value)
    {
        return BinaryArray<PgTimeTz, TimeTzCodec>.Measure(value.Span);
    }
    public static int GetByteCount(int elementCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elementCount);
        return ArrayPayload.Measure(elementCount, checked(elementCount * 12));
    }
    public static int Write(ReadOnlyMemory<PgTimeTz> value, Span<byte> destination)
    {
        return BinaryArray<PgTimeTz, TimeTzCodec>.Write(value, destination);
    }
    public static void Write(ReadOnlyMemory<PgTimeTz> value, IBufferWriter<byte> destination)
    {
        BinaryArray<PgTimeTz, TimeTzCodec>.Write(value, destination);
    }
    public static ReadOnlyMemory<PgTimeTz> Read(ReadOnlySpan<byte> payload)
    {
        return BinaryArray<PgTimeTz, TimeTzCodec>.Read(payload);
    }
    public static ReadOnlyMemory<PgTimeTz> Read(ReadOnlySequence<byte> payload)
    {
        return BinaryArray<PgTimeTz, TimeTzCodec>.Read(payload);
    }
    public static int Read(ReadOnlySpan<byte> payload, Span<PgTimeTz> destination)
    {
        return BinaryArray<PgTimeTz, TimeTzCodec>.Read(payload, destination);
    }
    public static int Read(ReadOnlySequence<byte> payload, Span<PgTimeTz> destination)
    {
        return BinaryArray<PgTimeTz, TimeTzCodec>.Read(payload, destination);
    }
}