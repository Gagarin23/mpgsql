using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL TimestampTz[] with NULL elements for ReadOnlyMemory&lt;PgTimestampTz?&gt;.</summary>
/// <remarks>Same framing/buffer contract as TimestampTzArrayConverter; NULL elements have length -1 and no payload.</remarks>
public static partial class NullableTimestampTzArrayConverter
{
    public const uint ElementTypeOid = (uint)TypeOid.TimestampTz;
    public const uint ArrayTypeOid = (uint)TypeOid.TimestampTzArray;
    public static int GetByteCount(ReadOnlyMemory<PgTimestampTz?> value) => BinaryNullableArray<PgTimestampTz, TimestampTzCodec>.Measure(value.Span, out _);
    public static int GetByteCount(int elementCount, int nullCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elementCount);
        ArgumentOutOfRangeException.ThrowIfNegative(nullCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(nullCount, elementCount);
        return ArrayPayload.Measure(elementCount, checked((elementCount - nullCount) * 8));
    }
    public static int Write(ReadOnlyMemory<PgTimestampTz?> value, Span<byte> destination) => BinaryNullableArray<PgTimestampTz, TimestampTzCodec>.Write(value, destination);
    public static void Write(ReadOnlyMemory<PgTimestampTz?> value, IBufferWriter<byte> destination) => BinaryNullableArray<PgTimestampTz, TimestampTzCodec>.Write(value, destination);
    public static ReadOnlyMemory<PgTimestampTz?> Read(ReadOnlySpan<byte> payload) => BinaryNullableArray<PgTimestampTz, TimestampTzCodec>.Read(payload);
    public static ReadOnlyMemory<PgTimestampTz?> Read(ReadOnlySequence<byte> payload) => BinaryNullableArray<PgTimestampTz, TimestampTzCodec>.Read(payload);
    public static int Read(ReadOnlySpan<byte> payload, Span<PgTimestampTz?> destination) => BinaryNullableArray<PgTimestampTz, TimestampTzCodec>.Read(payload, destination);
    public static int Read(ReadOnlySequence<byte> payload, Span<PgTimestampTz?> destination) => BinaryNullableArray<PgTimestampTz, TimestampTzCodec>.Read(payload, destination);
}