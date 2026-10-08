using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Time[] with NULL elements for ReadOnlyMemory&lt;PgTime?&gt;.</summary>
/// <remarks>Same framing/buffer contract as TimeArrayConverter; NULL elements have length -1 and no payload.</remarks>
public static partial class NullableTimeArrayConverter
{
    public const uint ElementTypeOid = (uint)TypeOid.Time;
    public const uint ArrayTypeOid = (uint)TypeOid.TimeArray;
    public static int GetByteCount(ReadOnlyMemory<PgTime?> value)
    {
        return BinaryNullableArray<PgTime, TimeCodec>.Measure(value.Span, out _);
    }
    public static int GetByteCount(int elementCount, int nullCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elementCount);
        ArgumentOutOfRangeException.ThrowIfNegative(nullCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(nullCount, elementCount);
        return ArrayPayload.Measure(elementCount, checked((elementCount - nullCount) * 8));
    }
    public static int Write(ReadOnlyMemory<PgTime?> value, Span<byte> destination)
    {
        return BinaryNullableArray<PgTime, TimeCodec>.Write(value, destination);
    }
    public static void Write(ReadOnlyMemory<PgTime?> value, IBufferWriter<byte> destination)
    {
        BinaryNullableArray<PgTime, TimeCodec>.Write(value, destination);
    }
    public static ReadOnlyMemory<PgTime?> Read(ReadOnlySpan<byte> payload)
    {
        return BinaryNullableArray<PgTime, TimeCodec>.Read(payload);
    }
    public static ReadOnlyMemory<PgTime?> Read(ReadOnlySequence<byte> payload)
    {
        return BinaryNullableArray<PgTime, TimeCodec>.Read(payload);
    }
    public static int Read(ReadOnlySpan<byte> payload, Span<PgTime?> destination)
    {
        return BinaryNullableArray<PgTime, TimeCodec>.Read(payload, destination);
    }
    public static int Read(ReadOnlySequence<byte> payload, Span<PgTime?> destination)
    {
        return BinaryNullableArray<PgTime, TimeCodec>.Read(payload, destination);
    }
}