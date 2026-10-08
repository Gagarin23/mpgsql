using System.Buffers;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Bytea[] for ReadOnlyMemory&lt;ReadOnlyMemory&lt;byte&gt;&gt;.</summary>
/// <remarks>
///     Supports empty and one-dimensional arrays; lower bounds are normalized to zero-based memory.
///     Writes only payload, with big-endian headers and element lengths. Capacity/overlap are checked
///     before writing. Read returns owned storage, or fills reusable storage without a payload copy.
///     An invalid element may leave reusable output partially written. SQL NULL is an outer field marker.
/// </remarks>
public static partial class ByteaArrayConverter
{
    public const uint ElementTypeOid = (uint)TypeOid.Bytea;
    public const uint ArrayTypeOid = (uint)TypeOid.ByteaArray;
    public static int GetByteCount(ReadOnlyMemory<ReadOnlyMemory<byte>> value)
    {
        return BinaryArray<ReadOnlyMemory<byte>, ByteaCodec>.Measure(value.Span);
    }

    public static int Write(ReadOnlyMemory<ReadOnlyMemory<byte>> value, Span<byte> destination)
    {
        return BinaryArray<ReadOnlyMemory<byte>, ByteaCodec>.Write(value, destination);
    }
    public static void Write(ReadOnlyMemory<ReadOnlyMemory<byte>> value, IBufferWriter<byte> destination)
    {
        BinaryArray<ReadOnlyMemory<byte>, ByteaCodec>.Write(value, destination);
    }
    public static ReadOnlyMemory<ReadOnlyMemory<byte>> Read(ReadOnlySpan<byte> payload)
    {
        return BinaryArray<ReadOnlyMemory<byte>, ByteaCodec>.Read(payload);
    }
    public static ReadOnlyMemory<ReadOnlyMemory<byte>> Read(ReadOnlySequence<byte> payload)
    {
        return BinaryArray<ReadOnlyMemory<byte>, ByteaCodec>.Read(payload);
    }
    public static int Read(ReadOnlySpan<byte> payload, Span<ReadOnlyMemory<byte>> destination)
    {
        return BinaryArray<ReadOnlyMemory<byte>, ByteaCodec>.Read(payload, destination);
    }
    public static int Read(ReadOnlySequence<byte> payload, Span<ReadOnlyMemory<byte>> destination)
    {
        return BinaryArray<ReadOnlyMemory<byte>, ByteaCodec>.Read(payload, destination);
    }
}