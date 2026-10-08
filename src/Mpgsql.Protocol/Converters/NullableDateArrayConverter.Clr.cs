using System.Buffers;

namespace Mpgsql.Converters;

public static partial class NullableDateArrayConverter
{
    public static int GetByteCount(ReadOnlyMemory<DateOnly?> value)
    {
        return BinaryNullableArray<DateOnly, DateClrCodec>.Measure(value.Span, out _);
    }
    public static int Write(ReadOnlyMemory<DateOnly?> value, Span<byte> destination)
    {
        return BinaryNullableArray<DateOnly, DateClrCodec>.Write(value, destination);
    }
    public static void Write(ReadOnlyMemory<DateOnly?> value, IBufferWriter<byte> destination)
    {
        BinaryNullableArray<DateOnly, DateClrCodec>.Write(value, destination);
    }
    public static ReadOnlyMemory<DateOnly?> ReadDateOnlys(ReadOnlySpan<byte> payload)
    {
        return BinaryNullableArray<DateOnly, DateClrCodec>.Read(payload);
    }
    public static ReadOnlyMemory<DateOnly?> ReadDateOnlys(ReadOnlySequence<byte> payload)
    {
        return BinaryNullableArray<DateOnly, DateClrCodec>.Read(payload);
    }
    public static int Read(ReadOnlySpan<byte> payload, Span<DateOnly?> destination)
    {
        return BinaryNullableArray<DateOnly, DateClrCodec>.Read(payload, destination);
    }
    public static int Read(ReadOnlySequence<byte> payload, Span<DateOnly?> destination)
    {
        return BinaryNullableArray<DateOnly, DateClrCodec>.Read(payload, destination);
    }
}