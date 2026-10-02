using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Inet[] with NULL elements for ReadOnlyMemory&lt;PgInet?&gt;.</summary>
/// <remarks>Same framing/buffer contract as InetArrayConverter; NULL elements have length -1 and no payload.</remarks>
public static partial class NullableInetArrayConverter
{
    public const uint ElementTypeOid = (uint)TypeOid.Inet;
    public const uint ArrayTypeOid = (uint)TypeOid.InetArray;
    public static int GetByteCount(ReadOnlyMemory<PgInet?> value) => BinaryNullableArray<PgInet, InetCodec>.Measure(value.Span, out _);

    public static int Write(ReadOnlyMemory<PgInet?> value, Span<byte> destination) => BinaryNullableArray<PgInet, InetCodec>.Write(value, destination);
    public static void Write(ReadOnlyMemory<PgInet?> value, IBufferWriter<byte> destination) => BinaryNullableArray<PgInet, InetCodec>.Write(value, destination);
    public static ReadOnlyMemory<PgInet?> Read(ReadOnlySpan<byte> payload) => BinaryNullableArray<PgInet, InetCodec>.Read(payload);
    public static ReadOnlyMemory<PgInet?> Read(ReadOnlySequence<byte> payload) => BinaryNullableArray<PgInet, InetCodec>.Read(payload);
    public static int Read(ReadOnlySpan<byte> payload, Span<PgInet?> destination) => BinaryNullableArray<PgInet, InetCodec>.Read(payload, destination);
    public static int Read(ReadOnlySequence<byte> payload, Span<PgInet?> destination) => BinaryNullableArray<PgInet, InetCodec>.Read(payload, destination);
}