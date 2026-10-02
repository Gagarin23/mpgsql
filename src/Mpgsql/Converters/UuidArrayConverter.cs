using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Uuid[] for ReadOnlyMemory&lt;Guid&gt;.</summary>
/// <remarks>
/// Supports empty and one-dimensional arrays; lower bounds are normalized to zero-based memory.
/// Writes only payload, with big-endian headers and element lengths. Capacity/overlap are checked
/// before writing. Read returns owned storage, or fills reusable storage without a payload copy.
/// An invalid element may leave reusable output partially written. SQL NULL is an outer field marker.
/// </remarks>
public static partial class UuidArrayConverter
{
    public const uint ElementTypeOid = (uint)TypeOid.Uuid;
    public const uint ArrayTypeOid = (uint)TypeOid.UuidArray;
    public static int GetByteCount(ReadOnlyMemory<Guid> value) => BinaryArray<Guid, UuidCodec>.Measure(value.Span);
    public static int GetByteCount(int elementCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elementCount);
        return ArrayPayload.Measure(elementCount, checked(elementCount * 16));
    }
    public static int Write(ReadOnlyMemory<Guid> value, Span<byte> destination) => BinaryArray<Guid, UuidCodec>.Write(value, destination);
    public static void Write(ReadOnlyMemory<Guid> value, IBufferWriter<byte> destination) => BinaryArray<Guid, UuidCodec>.Write(value, destination);
    public static ReadOnlyMemory<Guid> Read(ReadOnlySpan<byte> payload) => BinaryArray<Guid, UuidCodec>.Read(payload);
    public static ReadOnlyMemory<Guid> Read(ReadOnlySequence<byte> payload) => BinaryArray<Guid, UuidCodec>.Read(payload);
    public static int Read(ReadOnlySpan<byte> payload, Span<Guid> destination) => BinaryArray<Guid, UuidCodec>.Read(payload, destination);
    public static int Read(ReadOnlySequence<byte> payload, Span<Guid> destination) => BinaryArray<Guid, UuidCodec>.Read(payload, destination);
}