using System.Buffers;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Json[] for ReadOnlyMemory&lt;string?&gt;.</summary>
/// <remarks>
///     Supports empty and one-dimensional arrays; lower bounds are normalized to zero-based memory.
///     Writes only payload, with big-endian headers and element lengths. Capacity/overlap are checked
///     before writing. Read returns owned storage, or fills reusable storage without a payload copy.
///     An invalid element may leave reusable output partially written. SQL NULL is an outer field marker.
/// </remarks>
public static class JsonArrayConverter
{
    public const uint ElementTypeOid = (uint)TypeOid.Json;
    public const uint ArrayTypeOid = (uint)TypeOid.JsonArray;
    public static int GetByteCount(ReadOnlyMemory<string?> value)
    {
        return BinaryReferenceArray<string, JsonCodec>.Measure(value.Span, out _);
    }

    public static int Write(ReadOnlyMemory<string?> value, Span<byte> destination)
    {
        return BinaryReferenceArray<string, JsonCodec>.Write(value, destination);
    }
    public static void Write(ReadOnlyMemory<string?> value, IBufferWriter<byte> destination)
    {
        BinaryReferenceArray<string, JsonCodec>.Write(value, destination);
    }
    public static ReadOnlyMemory<string?> Read(ReadOnlySpan<byte> payload)
    {
        return BinaryReferenceArray<string, JsonCodec>.Read(payload);
    }
    public static ReadOnlyMemory<string?> Read(ReadOnlySequence<byte> payload)
    {
        return BinaryReferenceArray<string, JsonCodec>.Read(payload);
    }
    public static int Read(ReadOnlySpan<byte> payload, Span<string?> destination)
    {
        return BinaryReferenceArray<string, JsonCodec>.Read(payload, destination);
    }
    public static int Read(ReadOnlySequence<byte> payload, Span<string?> destination)
    {
        return BinaryReferenceArray<string, JsonCodec>.Read(payload, destination);
    }
}