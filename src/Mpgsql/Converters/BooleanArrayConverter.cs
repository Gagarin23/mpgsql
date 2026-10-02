using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Boolean[] for ReadOnlyMemory&lt;bool&gt;.</summary>
/// <remarks>
/// Supports empty and one-dimensional arrays; lower bounds are normalized to zero-based memory.
/// Writes only payload, with big-endian headers and element lengths. Capacity/overlap are checked
/// before writing. Read returns owned storage, or fills reusable storage without a payload copy.
/// An invalid element may leave reusable output partially written. SQL NULL is an outer field marker.
/// </remarks>
public static partial class BooleanArrayConverter
{
    public const uint ElementTypeOid = (uint)TypeOid.Boolean;
    public const uint ArrayTypeOid = (uint)TypeOid.BooleanArray;
    public static int GetByteCount(ReadOnlyMemory<bool> value) => BinaryArray<bool, BooleanCodec>.Measure(value.Span);
    public static int GetByteCount(int elementCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elementCount);
        return ArrayPayload.Measure(elementCount, checked(elementCount * 1));
    }
    public static int Write(ReadOnlyMemory<bool> value, Span<byte> destination) => BinaryArray<bool, BooleanCodec>.Write(value, destination);
    public static void Write(ReadOnlyMemory<bool> value, IBufferWriter<byte> destination) => BinaryArray<bool, BooleanCodec>.Write(value, destination);
    public static ReadOnlyMemory<bool> Read(ReadOnlySpan<byte> payload) => BinaryArray<bool, BooleanCodec>.Read(payload);
    public static ReadOnlyMemory<bool> Read(ReadOnlySequence<byte> payload) => BinaryArray<bool, BooleanCodec>.Read(payload);
    public static int Read(ReadOnlySpan<byte> payload, Span<bool> destination) => BinaryArray<bool, BooleanCodec>.Read(payload, destination);
    public static int Read(ReadOnlySequence<byte> payload, Span<bool> destination) => BinaryArray<bool, BooleanCodec>.Read(payload, destination);
}