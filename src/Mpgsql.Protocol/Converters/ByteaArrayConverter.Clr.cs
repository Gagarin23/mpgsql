using System.Buffers;

namespace Mpgsql.Converters;

public static partial class ByteaArrayConverter
{
    public static int GetByteCount(ReadOnlyMemory<byte[]?> value)
    {
        return BinaryReferenceArray<byte[], ByteArrayCodec>.Measure(value.Span, out _);
    }
    public static int Write(ReadOnlyMemory<byte[]?> value, Span<byte> destination)
    {
        return BinaryReferenceArray<byte[], ByteArrayCodec>.Write(value, destination);
    }
    public static void Write(ReadOnlyMemory<byte[]?> value, IBufferWriter<byte> destination)
    {
        BinaryReferenceArray<byte[], ByteArrayCodec>.Write(value, destination);
    }
    public static ReadOnlyMemory<byte[]?> ReadByteArrays(ReadOnlySpan<byte> payload)
    {
        return BinaryReferenceArray<byte[], ByteArrayCodec>.Read(payload);
    }
    public static ReadOnlyMemory<byte[]?> ReadByteArrays(ReadOnlySequence<byte> payload)
    {
        return BinaryReferenceArray<byte[], ByteArrayCodec>.Read(payload);
    }
    public static int Read(ReadOnlySpan<byte> payload, Span<byte[]?> destination)
    {
        return BinaryReferenceArray<byte[], ByteArrayCodec>.Read(payload, destination);
    }
    public static int Read(ReadOnlySequence<byte> payload, Span<byte[]?> destination)
    {
        return BinaryReferenceArray<byte[], ByteArrayCodec>.Read(payload, destination);
    }
}