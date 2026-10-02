using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL TimeTz[] with NULL elements for ReadOnlyMemory&lt;PgTimeTz?&gt;.</summary>
/// <remarks>Same framing/buffer contract as TimeTzArrayConverter; NULL elements have length -1 and no payload.</remarks>
public static partial class NullableTimeTzArrayConverter
{
    public const uint ElementTypeOid = (uint)TypeOid.TimeTz;
    public const uint ArrayTypeOid = (uint)TypeOid.TimeTzArray;
    public static int GetByteCount(ReadOnlyMemory<PgTimeTz?> value) => BinaryNullableArray<PgTimeTz, TimeTzCodec>.Measure(value.Span, out _);
    public static int GetByteCount(int elementCount, int nullCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elementCount);
        ArgumentOutOfRangeException.ThrowIfNegative(nullCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(nullCount, elementCount);
        return ArrayPayload.Measure(elementCount, checked((elementCount - nullCount) * 12));
    }
    public static int Write(ReadOnlyMemory<PgTimeTz?> value, Span<byte> destination) => BinaryNullableArray<PgTimeTz, TimeTzCodec>.Write(value, destination);
    public static void Write(ReadOnlyMemory<PgTimeTz?> value, IBufferWriter<byte> destination) => BinaryNullableArray<PgTimeTz, TimeTzCodec>.Write(value, destination);
    public static ReadOnlyMemory<PgTimeTz?> Read(ReadOnlySpan<byte> payload) => BinaryNullableArray<PgTimeTz, TimeTzCodec>.Read(payload);
    public static ReadOnlyMemory<PgTimeTz?> Read(ReadOnlySequence<byte> payload) => BinaryNullableArray<PgTimeTz, TimeTzCodec>.Read(payload);
    public static int Read(ReadOnlySpan<byte> payload, Span<PgTimeTz?> destination) => BinaryNullableArray<PgTimeTz, TimeTzCodec>.Read(payload, destination);
    public static int Read(ReadOnlySequence<byte> payload, Span<PgTimeTz?> destination) => BinaryNullableArray<PgTimeTz, TimeTzCodec>.Read(payload, destination);
}