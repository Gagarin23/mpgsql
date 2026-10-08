using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Time[] for ReadOnlyMemory&lt;PgTime&gt;.</summary>
/// <remarks>
///     Supports empty and one-dimensional arrays; lower bounds are normalized to zero-based memory.
///     Writes only payload, with big-endian headers and element lengths. Capacity/overlap are checked
///     before writing. Read returns owned storage, or fills reusable storage without a payload copy.
///     An invalid element may leave reusable output partially written. SQL NULL is an outer field marker.
/// </remarks>
public static partial class TimeArrayConverter
{
    public const uint ElementTypeOid = (uint)TypeOid.Time;
    public const uint ArrayTypeOid = (uint)TypeOid.TimeArray;
    public static int GetByteCount(ReadOnlyMemory<PgTime> value)
    {
        return BinaryArray<PgTime, TimeCodec>.Measure(value.Span);
    }
    public static int GetByteCount(int elementCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elementCount);
        return ArrayPayload.Measure(elementCount, checked(elementCount * 8));
    }
    public static int Write(ReadOnlyMemory<PgTime> value, Span<byte> destination)
    {
        return BinaryArray<PgTime, TimeCodec>.Write(value, destination);
    }
    public static void Write(ReadOnlyMemory<PgTime> value, IBufferWriter<byte> destination)
    {
        BinaryArray<PgTime, TimeCodec>.Write(value, destination);
    }
    public static ReadOnlyMemory<PgTime> Read(ReadOnlySpan<byte> payload)
    {
        return BinaryArray<PgTime, TimeCodec>.Read(payload);
    }
    public static ReadOnlyMemory<PgTime> Read(ReadOnlySequence<byte> payload)
    {
        return BinaryArray<PgTime, TimeCodec>.Read(payload);
    }
    public static int Read(ReadOnlySpan<byte> payload, Span<PgTime> destination)
    {
        return BinaryArray<PgTime, TimeCodec>.Read(payload, destination);
    }
    public static int Read(ReadOnlySequence<byte> payload, Span<PgTime> destination)
    {
        return BinaryArray<PgTime, TimeCodec>.Read(payload, destination);
    }
}