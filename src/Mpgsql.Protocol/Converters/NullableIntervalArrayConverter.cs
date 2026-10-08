using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Interval[] with NULL elements for ReadOnlyMemory&lt;PgInterval?&gt;.</summary>
/// <remarks>Same framing/buffer contract as IntervalArrayConverter; NULL elements have length -1 and no payload.</remarks>
public static partial class NullableIntervalArrayConverter
{
    public const uint ElementTypeOid = (uint)TypeOid.Interval;
    public const uint ArrayTypeOid = (uint)TypeOid.IntervalArray;
    public static int GetByteCount(ReadOnlyMemory<PgInterval?> value)
    {
        return BinaryNullableArray<PgInterval, IntervalCodec>.Measure(value.Span, out _);
    }
    public static int GetByteCount(int elementCount, int nullCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elementCount);
        ArgumentOutOfRangeException.ThrowIfNegative(nullCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(nullCount, elementCount);
        return ArrayPayload.Measure(elementCount, checked((elementCount - nullCount) * 16));
    }
    public static int Write(ReadOnlyMemory<PgInterval?> value, Span<byte> destination)
    {
        return BinaryNullableArray<PgInterval, IntervalCodec>.Write(value, destination);
    }
    public static void Write(ReadOnlyMemory<PgInterval?> value, IBufferWriter<byte> destination)
    {
        BinaryNullableArray<PgInterval, IntervalCodec>.Write(value, destination);
    }
    public static ReadOnlyMemory<PgInterval?> Read(ReadOnlySpan<byte> payload)
    {
        return BinaryNullableArray<PgInterval, IntervalCodec>.Read(payload);
    }
    public static ReadOnlyMemory<PgInterval?> Read(ReadOnlySequence<byte> payload)
    {
        return BinaryNullableArray<PgInterval, IntervalCodec>.Read(payload);
    }
    public static int Read(ReadOnlySpan<byte> payload, Span<PgInterval?> destination)
    {
        return BinaryNullableArray<PgInterval, IntervalCodec>.Read(payload, destination);
    }
    public static int Read(ReadOnlySequence<byte> payload, Span<PgInterval?> destination)
    {
        return BinaryNullableArray<PgInterval, IntervalCodec>.Read(payload, destination);
    }
}