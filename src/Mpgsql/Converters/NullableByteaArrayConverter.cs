using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Bytea[] with NULL elements for ReadOnlyMemory&lt;ReadOnlyMemory&lt;byte&gt;?&gt;.</summary>
/// <remarks>Same framing/buffer contract as ByteaArrayConverter; NULL elements have length -1 and no payload.</remarks>
public static partial class NullableByteaArrayConverter
{
    public const uint ElementTypeOid = (uint)TypeOid.Bytea;
    public const uint ArrayTypeOid = (uint)TypeOid.ByteaArray;
    public static int GetByteCount(ReadOnlyMemory<ReadOnlyMemory<byte>?> value) => BinaryNullableArray<ReadOnlyMemory<byte>, ByteaCodec>.Measure(value.Span, out _);

    public static int Write(ReadOnlyMemory<ReadOnlyMemory<byte>?> value, Span<byte> destination) => BinaryNullableArray<ReadOnlyMemory<byte>, ByteaCodec>.Write(value, destination);
    public static void Write(ReadOnlyMemory<ReadOnlyMemory<byte>?> value, IBufferWriter<byte> destination) => BinaryNullableArray<ReadOnlyMemory<byte>, ByteaCodec>.Write(value, destination);
    public static ReadOnlyMemory<ReadOnlyMemory<byte>?> Read(ReadOnlySpan<byte> payload) => BinaryNullableArray<ReadOnlyMemory<byte>, ByteaCodec>.Read(payload);
    public static ReadOnlyMemory<ReadOnlyMemory<byte>?> Read(ReadOnlySequence<byte> payload) => BinaryNullableArray<ReadOnlyMemory<byte>, ByteaCodec>.Read(payload);
    public static int Read(ReadOnlySpan<byte> payload, Span<ReadOnlyMemory<byte>?> destination) => BinaryNullableArray<ReadOnlyMemory<byte>, ByteaCodec>.Read(payload, destination);
    public static int Read(ReadOnlySequence<byte> payload, Span<ReadOnlyMemory<byte>?> destination) => BinaryNullableArray<ReadOnlyMemory<byte>, ByteaCodec>.Read(payload, destination);
}
