using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Timestamp[] for ReadOnlyMemory&lt;PgTimestamp&gt;.</summary>
/// <remarks>
///     Supports empty and one-dimensional arrays; lower bounds are normalized to zero-based memory.
///     Writes only payload, with big-endian headers and element lengths. Capacity/overlap are checked
///     before writing. Read returns owned storage, or fills reusable storage without a payload copy.
///     An invalid element may leave reusable output partially written. SQL NULL is an outer field marker.
/// </remarks>
public static partial class TimestampArrayConverter
{
    public const uint ElementTypeOid = (uint)TypeOid.Timestamp;
    public const uint ArrayTypeOid = (uint)TypeOid.TimestampArray;
    public static int GetByteCount(ReadOnlyMemory<PgTimestamp> value)
    {
        return BinaryArray<PgTimestamp, TimestampCodec>.Measure(value.Span);
    }
    public static int GetByteCount(int elementCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elementCount);
        return ArrayPayload.Measure(elementCount, checked(elementCount * 8));
    }
    public static int Write(ReadOnlyMemory<PgTimestamp> value, Span<byte> destination)
    {
        return BinaryArray<PgTimestamp, TimestampCodec>.Write(value, destination);
    }
    public static void Write(ReadOnlyMemory<PgTimestamp> value, IBufferWriter<byte> destination)
    {
        BinaryArray<PgTimestamp, TimestampCodec>.Write(value, destination);
    }
    public static ReadOnlyMemory<PgTimestamp> Read(ReadOnlySpan<byte> payload)
    {
        return BinaryArray<PgTimestamp, TimestampCodec>.Read(payload);
    }
    public static ReadOnlyMemory<PgTimestamp> Read(ReadOnlySequence<byte> payload)
    {
        return BinaryArray<PgTimestamp, TimestampCodec>.Read(payload);
    }
    public static int Read(ReadOnlySpan<byte> payload, Span<PgTimestamp> destination)
    {
        return BinaryArray<PgTimestamp, TimestampCodec>.Read(payload, destination);
    }
    public static int Read(ReadOnlySequence<byte> payload, Span<PgTimestamp> destination)
    {
        return BinaryArray<PgTimestamp, TimestampCodec>.Read(payload, destination);
    }
}