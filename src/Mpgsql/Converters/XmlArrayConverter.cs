using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Xml[] for ReadOnlyMemory&lt;string?&gt;.</summary>
/// <remarks>
/// Supports empty and one-dimensional arrays; lower bounds are normalized to zero-based memory.
/// Writes only payload, with big-endian headers and element lengths. Capacity/overlap are checked
/// before writing. Read returns owned storage, or fills reusable storage without a payload copy.
/// An invalid element may leave reusable output partially written. SQL NULL is an outer field marker.
/// </remarks>
public static partial class XmlArrayConverter
{
    public const uint ElementTypeOid = (uint)TypeOid.Xml;
    public const uint ArrayTypeOid = (uint)TypeOid.XmlArray;
    public static int GetByteCount(ReadOnlyMemory<string?> value) => BinaryReferenceArray<string, XmlCodec>.Measure(value.Span, out _);

    public static int Write(ReadOnlyMemory<string?> value, Span<byte> destination) => BinaryReferenceArray<string, XmlCodec>.Write(value, destination);
    public static void Write(ReadOnlyMemory<string?> value, IBufferWriter<byte> destination) => BinaryReferenceArray<string, XmlCodec>.Write(value, destination);
    public static ReadOnlyMemory<string?> Read(ReadOnlySpan<byte> payload) => BinaryReferenceArray<string, XmlCodec>.Read(payload);
    public static ReadOnlyMemory<string?> Read(ReadOnlySequence<byte> payload) => BinaryReferenceArray<string, XmlCodec>.Read(payload);
    public static int Read(ReadOnlySpan<byte> payload, Span<string?> destination) => BinaryReferenceArray<string, XmlCodec>.Read(payload, destination);
    public static int Read(ReadOnlySequence<byte> payload, Span<string?> destination) => BinaryReferenceArray<string, XmlCodec>.Read(payload, destination);
}