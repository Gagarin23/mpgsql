using System.Buffers;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL jsonb[] for ReadOnlyMemory&lt;Memory&lt;byte&gt;&gt;.</summary>
/// <remarks>
///     Supports empty and one-dimensional arrays; lower bounds are normalized to zero-based memory.
///     Writes only payload, with big-endian headers and element lengths. Capacity/overlap are checked
///     before writing. Read copies UTF-8 JSON bytes into owned memory for each element,
///     and either allocates or fills reusable outer storage. Use NullableJsonbArrayConverter for NULL elements.
///     An invalid element may leave reusable output partially written. SQL NULL is an outer field marker.
/// </remarks>
public static class JsonbArrayConverter
{
    public const uint ElementTypeOid = (uint)TypeOid.Jsonb;
    public const uint ArrayTypeOid = (uint)TypeOid.JsonbArray;
    public static int GetByteCount(ReadOnlyMemory<Memory<byte>> value)
    {
        return BinaryArray<Memory<byte>, JsonbCodec>.Measure(value.Span);
    }

    public static int Write(ReadOnlyMemory<Memory<byte>> value, Span<byte> destination)
    {
        return BinaryArray<Memory<byte>, JsonbCodec>.Write(value, destination);
    }
    public static void Write(ReadOnlyMemory<Memory<byte>> value, IBufferWriter<byte> destination)
    {
        BinaryArray<Memory<byte>, JsonbCodec>.Write(value, destination);
    }
    public static ReadOnlyMemory<Memory<byte>> Read(ReadOnlySpan<byte> payload)
    {
        return BinaryArray<Memory<byte>, JsonbCodec>.Read(payload);
    }
    public static ReadOnlyMemory<Memory<byte>> Read(ReadOnlySequence<byte> payload)
    {
        return BinaryArray<Memory<byte>, JsonbCodec>.Read(payload);
    }
    public static int Read(ReadOnlySpan<byte> payload, Span<Memory<byte>> destination)
    {
        return BinaryArray<Memory<byte>, JsonbCodec>.Read(payload, destination);
    }
    public static int Read(ReadOnlySequence<byte> payload, Span<Memory<byte>> destination)
    {
        return BinaryArray<Memory<byte>, JsonbCodec>.Read(payload, destination);
    }
}