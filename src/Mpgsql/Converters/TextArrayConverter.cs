using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Text[] for ReadOnlyMemory&lt;string?&gt;.</summary>
/// <remarks>
/// Supports empty and one-dimensional arrays; lower bounds are normalized to zero-based memory.
/// Writes only payload, with big-endian headers and element lengths. Capacity/overlap are checked
/// before writing. Read returns owned storage, or fills reusable storage without a payload copy.
/// An invalid element may leave reusable output partially written. SQL NULL is an outer field marker.
/// </remarks>
public static partial class TextArrayConverter
{
    public const uint ElementTypeOid = (uint)TypeOid.Text;
    public const uint ArrayTypeOid = (uint)TypeOid.TextArray;
    public static int GetByteCount(ReadOnlyMemory<string?> value) => BinaryReferenceArray<string, TextCodec>.Measure(value.Span, out _);

    public static int Write(ReadOnlyMemory<string?> value, Span<byte> destination) => BinaryReferenceArray<string, TextCodec>.Write(value, destination);
    public static void Write(ReadOnlyMemory<string?> value, IBufferWriter<byte> destination) => BinaryReferenceArray<string, TextCodec>.Write(value, destination);
    public static ReadOnlyMemory<string?> Read(ReadOnlySpan<byte> payload) => BinaryReferenceArray<string, TextCodec>.Read(payload);
    public static ReadOnlyMemory<string?> Read(ReadOnlySequence<byte> payload) => BinaryReferenceArray<string, TextCodec>.Read(payload);
    public static int Read(ReadOnlySpan<byte> payload, Span<string?> destination) => BinaryReferenceArray<string, TextCodec>.Read(payload, destination);
    public static int Read(ReadOnlySequence<byte> payload, Span<string?> destination) => BinaryReferenceArray<string, TextCodec>.Read(payload, destination);
}