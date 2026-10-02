using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Interval[] for ReadOnlyMemory&lt;PgInterval&gt;.</summary>
/// <remarks>
/// Supports empty and one-dimensional arrays; lower bounds are normalized to zero-based memory.
/// Writes only payload, with big-endian headers and element lengths. Capacity/overlap are checked
/// before writing. Read returns owned storage, or fills reusable storage without a payload copy.
/// An invalid element may leave reusable output partially written. SQL NULL is an outer field marker.
/// </remarks>
public static partial class IntervalArrayConverter
{
    public const uint ElementTypeOid = (uint)TypeOid.Interval;
    public const uint ArrayTypeOid = (uint)TypeOid.IntervalArray;
    public static int GetByteCount(ReadOnlyMemory<PgInterval> value) => BinaryArray<PgInterval, IntervalCodec>.Measure(value.Span);
    public static int GetByteCount(int elementCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elementCount);
        return ArrayPayload.Measure(elementCount, checked(elementCount * 16));
    }
    public static int Write(ReadOnlyMemory<PgInterval> value, Span<byte> destination) => BinaryArray<PgInterval, IntervalCodec>.Write(value, destination);
    public static void Write(ReadOnlyMemory<PgInterval> value, IBufferWriter<byte> destination) => BinaryArray<PgInterval, IntervalCodec>.Write(value, destination);
    public static ReadOnlyMemory<PgInterval> Read(ReadOnlySpan<byte> payload) => BinaryArray<PgInterval, IntervalCodec>.Read(payload);
    public static ReadOnlyMemory<PgInterval> Read(ReadOnlySequence<byte> payload) => BinaryArray<PgInterval, IntervalCodec>.Read(payload);
    public static int Read(ReadOnlySpan<byte> payload, Span<PgInterval> destination) => BinaryArray<PgInterval, IntervalCodec>.Read(payload, destination);
    public static int Read(ReadOnlySequence<byte> payload, Span<PgInterval> destination) => BinaryArray<PgInterval, IntervalCodec>.Read(payload, destination);
}