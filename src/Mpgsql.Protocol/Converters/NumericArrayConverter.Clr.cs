using System.Buffers;

namespace Mpgsql.Converters;

public static partial class NumericArrayConverter
{
    public static int GetByteCount(ReadOnlyMemory<decimal> value)
    {
        return BinaryArray<decimal, DecimalCodec>.Measure(value.Span);
    }
    public static int Write(ReadOnlyMemory<decimal> value, Span<byte> destination)
    {
        return BinaryArray<decimal, DecimalCodec>.Write(value, destination);
    }
    public static void Write(ReadOnlyMemory<decimal> value, IBufferWriter<byte> destination)
    {
        BinaryArray<decimal, DecimalCodec>.Write(value, destination);
    }
    public static ReadOnlyMemory<decimal> ReadDecimals(ReadOnlySpan<byte> payload)
    {
        return BinaryArray<decimal, DecimalCodec>.Read(payload);
    }
    public static ReadOnlyMemory<decimal> ReadDecimals(ReadOnlySequence<byte> payload)
    {
        return BinaryArray<decimal, DecimalCodec>.Read(payload);
    }
    public static int Read(ReadOnlySpan<byte> payload, Span<decimal> destination)
    {
        return BinaryArray<decimal, DecimalCodec>.Read(payload, destination);
    }
    public static int Read(ReadOnlySequence<byte> payload, Span<decimal> destination)
    {
        return BinaryArray<decimal, DecimalCodec>.Read(payload, destination);
    }
}