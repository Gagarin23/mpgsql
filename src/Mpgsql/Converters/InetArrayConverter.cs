using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Inet[] for ReadOnlyMemory&lt;PgInet&gt;.</summary>
/// <remarks>
/// Supports empty and one-dimensional arrays; lower bounds are normalized to zero-based memory.
/// Writes only payload, with big-endian headers and element lengths. Capacity/overlap are checked
/// before writing. Read returns owned storage, or fills reusable storage without a payload copy.
/// An invalid element may leave reusable output partially written. SQL NULL is an outer field marker.
/// </remarks>
public static partial class InetArrayConverter
{
    public const uint ElementTypeOid = (uint)TypeOid.Inet;
    public const uint ArrayTypeOid = (uint)TypeOid.InetArray;
    public static int GetByteCount(ReadOnlyMemory<PgInet> value) => BinaryArray<PgInet, InetCodec>.Measure(value.Span);

    public static int Write(ReadOnlyMemory<PgInet> value, Span<byte> destination) => BinaryArray<PgInet, InetCodec>.Write(value, destination);
    public static void Write(ReadOnlyMemory<PgInet> value, IBufferWriter<byte> destination) => BinaryArray<PgInet, InetCodec>.Write(value, destination);
    public static ReadOnlyMemory<PgInet> Read(ReadOnlySpan<byte> payload) => BinaryArray<PgInet, InetCodec>.Read(payload);
    public static ReadOnlyMemory<PgInet> Read(ReadOnlySequence<byte> payload) => BinaryArray<PgInet, InetCodec>.Read(payload);
    public static int Read(ReadOnlySpan<byte> payload, Span<PgInet> destination) => BinaryArray<PgInet, InetCodec>.Read(payload, destination);
    public static int Read(ReadOnlySequence<byte> payload, Span<PgInet> destination) => BinaryArray<PgInet, InetCodec>.Read(payload, destination);
}