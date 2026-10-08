using System.Buffers;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Oid[] with NULL elements for ReadOnlyMemory&lt;uint?&gt;.</summary>
/// <remarks>Same framing/buffer contract as OidArrayConverter; NULL elements have length -1 and no payload.</remarks>
public static class NullableOidArrayConverter
{
    public const uint ElementTypeOid = (uint)TypeOid.Oid;
    public const uint ArrayTypeOid = (uint)TypeOid.OidArray;
    public static int GetByteCount(ReadOnlyMemory<uint?> value)
    {
        return BinaryNullableArray<uint, OidCodec>.Measure(value.Span, out _);
    }
    public static int GetByteCount(int elementCount, int nullCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elementCount);
        ArgumentOutOfRangeException.ThrowIfNegative(nullCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(nullCount, elementCount);
        return ArrayPayload.Measure(elementCount, checked((elementCount - nullCount) * 4));
    }
    public static int Write(ReadOnlyMemory<uint?> value, Span<byte> destination)
    {
        return BinaryNullableArray<uint, OidCodec>.Write(value, destination);
    }
    public static void Write(ReadOnlyMemory<uint?> value, IBufferWriter<byte> destination)
    {
        BinaryNullableArray<uint, OidCodec>.Write(value, destination);
    }
    public static ReadOnlyMemory<uint?> Read(ReadOnlySpan<byte> payload)
    {
        return BinaryNullableArray<uint, OidCodec>.Read(payload);
    }
    public static ReadOnlyMemory<uint?> Read(ReadOnlySequence<byte> payload)
    {
        return BinaryNullableArray<uint, OidCodec>.Read(payload);
    }
    public static int Read(ReadOnlySpan<byte> payload, Span<uint?> destination)
    {
        return BinaryNullableArray<uint, OidCodec>.Read(payload, destination);
    }
    public static int Read(ReadOnlySequence<byte> payload, Span<uint?> destination)
    {
        return BinaryNullableArray<uint, OidCodec>.Read(payload, destination);
    }
}