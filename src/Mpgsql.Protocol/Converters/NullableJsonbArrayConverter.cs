using System.Buffers;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL jsonb[] with NULL elements for ReadOnlyMemory&lt;Memory&lt;byte&gt;?&gt;.</summary>
/// <remarks>Same framing/buffer contract as JsonbArrayConverter; NULL elements have length -1 and no payload.</remarks>
public static class NullableJsonbArrayConverter
{
    public const uint ElementTypeOid = (uint)TypeOid.Jsonb;
    public const uint ArrayTypeOid = (uint)TypeOid.JsonbArray;
    public static int GetByteCount(ReadOnlyMemory<Memory<byte>?> value)
    {
        return BinaryNullableArray<Memory<byte>, JsonbCodec>.Measure(value.Span, out _);
    }

    public static int Write(ReadOnlyMemory<Memory<byte>?> value, Span<byte> destination)
    {
        return BinaryNullableArray<Memory<byte>, JsonbCodec>.Write(value, destination);
    }
    public static void Write(ReadOnlyMemory<Memory<byte>?> value, IBufferWriter<byte> destination)
    {
        BinaryNullableArray<Memory<byte>, JsonbCodec>.Write(value, destination);
    }
    public static ReadOnlyMemory<Memory<byte>?> Read(ReadOnlySpan<byte> payload)
    {
        return BinaryNullableArray<Memory<byte>, JsonbCodec>.Read(payload);
    }
    public static ReadOnlyMemory<Memory<byte>?> Read(ReadOnlySequence<byte> payload)
    {
        return BinaryNullableArray<Memory<byte>, JsonbCodec>.Read(payload);
    }
    public static int Read(ReadOnlySpan<byte> payload, Span<Memory<byte>?> destination)
    {
        return BinaryNullableArray<Memory<byte>, JsonbCodec>.Read(payload, destination);
    }
    public static int Read(ReadOnlySequence<byte> payload, Span<Memory<byte>?> destination)
    {
        return BinaryNullableArray<Memory<byte>, JsonbCodec>.Read(payload, destination);
    }
}