using System.Buffers;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Uuid[] with NULL elements for ReadOnlyMemory&lt;Guid?&gt;.</summary>
/// <remarks>Same framing/buffer contract as UuidArrayConverter; NULL elements have length -1 and no payload.</remarks>
public static class NullableUuidArrayConverter
{
    public const uint ElementTypeOid = (uint)TypeOid.Uuid;
    public const uint ArrayTypeOid = (uint)TypeOid.UuidArray;
    public static int GetByteCount(ReadOnlyMemory<Guid?> value)
    {
        return BinaryNullableArray<Guid, UuidCodec>.Measure(value.Span, out _);
    }
    public static int GetByteCount(int elementCount, int nullCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elementCount);
        ArgumentOutOfRangeException.ThrowIfNegative(nullCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(nullCount, elementCount);
        return ArrayPayload.Measure(elementCount, checked((elementCount - nullCount) * 16));
    }
    public static int Write(ReadOnlyMemory<Guid?> value, Span<byte> destination)
    {
        return BinaryNullableArray<Guid, UuidCodec>.Write(value, destination);
    }
    public static void Write(ReadOnlyMemory<Guid?> value, IBufferWriter<byte> destination)
    {
        BinaryNullableArray<Guid, UuidCodec>.Write(value, destination);
    }
    public static ReadOnlyMemory<Guid?> Read(ReadOnlySpan<byte> payload)
    {
        return BinaryNullableArray<Guid, UuidCodec>.Read(payload);
    }
    public static ReadOnlyMemory<Guid?> Read(ReadOnlySequence<byte> payload)
    {
        return BinaryNullableArray<Guid, UuidCodec>.Read(payload);
    }
    public static int Read(ReadOnlySpan<byte> payload, Span<Guid?> destination)
    {
        return BinaryNullableArray<Guid, UuidCodec>.Read(payload, destination);
    }
    public static int Read(ReadOnlySequence<byte> payload, Span<Guid?> destination)
    {
        return BinaryNullableArray<Guid, UuidCodec>.Read(payload, destination);
    }
}