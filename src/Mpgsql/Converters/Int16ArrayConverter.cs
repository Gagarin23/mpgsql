using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Int16[] for ReadOnlyMemory&lt;short&gt;.</summary>
/// <remarks>
/// Supports empty and one-dimensional arrays; lower bounds are normalized to zero-based memory.
/// Writes only payload, with big-endian headers and element lengths. Capacity/overlap are checked
/// before writing. Read returns owned storage, or fills reusable storage without a payload copy.
/// An invalid element may leave reusable output partially written. SQL NULL is an outer field marker.
/// </remarks>
public static partial class Int16ArrayConverter
{
    public const uint ElementTypeOid = (uint)TypeOid.Int16;
    public const uint ArrayTypeOid = (uint)TypeOid.Int16Array;
    public static int GetByteCount(ReadOnlyMemory<short> value) => BinaryArray<short, Int16Codec>.Measure(value.Span);
    public static int GetByteCount(int elementCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elementCount);
        return ArrayPayload.Measure(elementCount, checked(elementCount * 2));
    }
    public static int Write(ReadOnlyMemory<short> value, Span<byte> destination) => BinaryArray<short, Int16Codec>.Write(value, destination);
    public static void Write(ReadOnlyMemory<short> value, IBufferWriter<byte> destination) => BinaryArray<short, Int16Codec>.Write(value, destination);
    public static ReadOnlyMemory<short> Read(ReadOnlySpan<byte> payload) => BinaryArray<short, Int16Codec>.Read(payload);
    public static ReadOnlyMemory<short> Read(ReadOnlySequence<byte> payload) => BinaryArray<short, Int16Codec>.Read(payload);
    public static int Read(ReadOnlySpan<byte> payload, Span<short> destination) => BinaryArray<short, Int16Codec>.Read(payload, destination);
    public static int Read(ReadOnlySequence<byte> payload, Span<short> destination) => BinaryArray<short, Int16Codec>.Read(payload, destination);
}