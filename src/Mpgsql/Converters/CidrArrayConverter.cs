using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Cidr[] for ReadOnlyMemory&lt;PgInet&gt;.</summary>
/// <remarks>
/// Supports empty and one-dimensional arrays; lower bounds are normalized to zero-based memory.
/// Writes only payload, with big-endian headers and element lengths. Capacity/overlap are checked
/// before writing. Read returns owned storage, or fills reusable storage without a payload copy.
/// An invalid element may leave reusable output partially written. SQL NULL is an outer field marker.
/// </remarks>
public static partial class CidrArrayConverter
{
    public const uint ElementTypeOid = (uint)TypeOid.Cidr;
    public const uint ArrayTypeOid = (uint)TypeOid.CidrArray;
    public static int GetByteCount(ReadOnlyMemory<PgInet> value) => BinaryArray<PgInet, CidrCodec>.Measure(value.Span);

    public static int Write(ReadOnlyMemory<PgInet> value, Span<byte> destination) => BinaryArray<PgInet, CidrCodec>.Write(value, destination);
    public static void Write(ReadOnlyMemory<PgInet> value, IBufferWriter<byte> destination) => BinaryArray<PgInet, CidrCodec>.Write(value, destination);
    public static ReadOnlyMemory<PgInet> Read(ReadOnlySpan<byte> payload) => BinaryArray<PgInet, CidrCodec>.Read(payload);
    public static ReadOnlyMemory<PgInet> Read(ReadOnlySequence<byte> payload) => BinaryArray<PgInet, CidrCodec>.Read(payload);
    public static int Read(ReadOnlySpan<byte> payload, Span<PgInet> destination) => BinaryArray<PgInet, CidrCodec>.Read(payload, destination);
    public static int Read(ReadOnlySequence<byte> payload, Span<PgInet> destination) => BinaryArray<PgInet, CidrCodec>.Read(payload, destination);
}