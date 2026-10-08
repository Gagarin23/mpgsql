using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Timestamp[] with NULL elements for ReadOnlyMemory&lt;PgTimestamp?&gt;.</summary>
/// <remarks>Same framing/buffer contract as TimestampArrayConverter; NULL elements have length -1 and no payload.</remarks>
public static partial class NullableTimestampArrayConverter
{
    public const uint ElementTypeOid = (uint)TypeOid.Timestamp;
    public const uint ArrayTypeOid = (uint)TypeOid.TimestampArray;
    public static int GetByteCount(ReadOnlyMemory<PgTimestamp?> value)
    {
        return BinaryNullableArray<PgTimestamp, TimestampCodec>.Measure(value.Span, out _);
    }
    public static int GetByteCount(int elementCount, int nullCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elementCount);
        ArgumentOutOfRangeException.ThrowIfNegative(nullCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(nullCount, elementCount);
        return ArrayPayload.Measure(elementCount, checked((elementCount - nullCount) * 8));
    }
    public static int Write(ReadOnlyMemory<PgTimestamp?> value, Span<byte> destination)
    {
        return BinaryNullableArray<PgTimestamp, TimestampCodec>.Write(value, destination);
    }
    public static void Write(ReadOnlyMemory<PgTimestamp?> value, IBufferWriter<byte> destination)
    {
        BinaryNullableArray<PgTimestamp, TimestampCodec>.Write(value, destination);
    }
    public static ReadOnlyMemory<PgTimestamp?> Read(ReadOnlySpan<byte> payload)
    {
        return BinaryNullableArray<PgTimestamp, TimestampCodec>.Read(payload);
    }
    public static ReadOnlyMemory<PgTimestamp?> Read(ReadOnlySequence<byte> payload)
    {
        return BinaryNullableArray<PgTimestamp, TimestampCodec>.Read(payload);
    }
    public static int Read(ReadOnlySpan<byte> payload, Span<PgTimestamp?> destination)
    {
        return BinaryNullableArray<PgTimestamp, TimestampCodec>.Read(payload, destination);
    }
    public static int Read(ReadOnlySequence<byte> payload, Span<PgTimestamp?> destination)
    {
        return BinaryNullableArray<PgTimestamp, TimestampCodec>.Read(payload, destination);
    }
}