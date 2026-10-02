using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL TimestampTz[] for ReadOnlyMemory&lt;PgTimestampTz&gt;.</summary>
/// <remarks>
/// Supports empty and one-dimensional arrays; lower bounds are normalized to zero-based memory.
/// Writes only payload, with big-endian headers and element lengths. Capacity/overlap are checked
/// before writing. Read returns owned storage, or fills reusable storage without a payload copy.
/// An invalid element may leave reusable output partially written. SQL NULL is an outer field marker.
/// </remarks>
public static partial class TimestampTzArrayConverter
{
    public const uint ElementTypeOid = (uint)TypeOid.TimestampTz;
    public const uint ArrayTypeOid = (uint)TypeOid.TimestampTzArray;
    public static int GetByteCount(ReadOnlyMemory<PgTimestampTz> value) => BinaryArray<PgTimestampTz, TimestampTzCodec>.Measure(value.Span);
    public static int GetByteCount(int elementCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elementCount);
        return ArrayPayload.Measure(elementCount, checked(elementCount * 8));
    }
    public static int Write(ReadOnlyMemory<PgTimestampTz> value, Span<byte> destination) => BinaryArray<PgTimestampTz, TimestampTzCodec>.Write(value, destination);
    public static void Write(ReadOnlyMemory<PgTimestampTz> value, IBufferWriter<byte> destination) => BinaryArray<PgTimestampTz, TimestampTzCodec>.Write(value, destination);
    public static ReadOnlyMemory<PgTimestampTz> Read(ReadOnlySpan<byte> payload) => BinaryArray<PgTimestampTz, TimestampTzCodec>.Read(payload);
    public static ReadOnlyMemory<PgTimestampTz> Read(ReadOnlySequence<byte> payload) => BinaryArray<PgTimestampTz, TimestampTzCodec>.Read(payload);
    public static int Read(ReadOnlySpan<byte> payload, Span<PgTimestampTz> destination) => BinaryArray<PgTimestampTz, TimestampTzCodec>.Read(payload, destination);
    public static int Read(ReadOnlySequence<byte> payload, Span<PgTimestampTz> destination) => BinaryArray<PgTimestampTz, TimestampTzCodec>.Read(payload, destination);
}