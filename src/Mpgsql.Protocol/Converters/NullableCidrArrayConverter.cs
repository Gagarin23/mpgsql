using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Cidr[] with NULL elements for ReadOnlyMemory&lt;PgInet?&gt;.</summary>
/// <remarks>Same framing/buffer contract as CidrArrayConverter; NULL elements have length -1 and no payload.</remarks>
public static class NullableCidrArrayConverter
{
    public const uint ElementTypeOid = (uint)TypeOid.Cidr;
    public const uint ArrayTypeOid = (uint)TypeOid.CidrArray;
    public static int GetByteCount(ReadOnlyMemory<PgInet?> value)
    {
        return BinaryNullableArray<PgInet, CidrCodec>.Measure(value.Span, out _);
    }

    public static int Write(ReadOnlyMemory<PgInet?> value, Span<byte> destination)
    {
        return BinaryNullableArray<PgInet, CidrCodec>.Write(value, destination);
    }
    public static void Write(ReadOnlyMemory<PgInet?> value, IBufferWriter<byte> destination)
    {
        BinaryNullableArray<PgInet, CidrCodec>.Write(value, destination);
    }
    public static ReadOnlyMemory<PgInet?> Read(ReadOnlySpan<byte> payload)
    {
        return BinaryNullableArray<PgInet, CidrCodec>.Read(payload);
    }
    public static ReadOnlyMemory<PgInet?> Read(ReadOnlySequence<byte> payload)
    {
        return BinaryNullableArray<PgInet, CidrCodec>.Read(payload);
    }
    public static int Read(ReadOnlySpan<byte> payload, Span<PgInet?> destination)
    {
        return BinaryNullableArray<PgInet, CidrCodec>.Read(payload, destination);
    }
    public static int Read(ReadOnlySequence<byte> payload, Span<PgInet?> destination)
    {
        return BinaryNullableArray<PgInet, CidrCodec>.Read(payload, destination);
    }
}