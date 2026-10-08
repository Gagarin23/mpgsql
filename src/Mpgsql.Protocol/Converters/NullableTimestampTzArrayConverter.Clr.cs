using System.Buffers;

namespace Mpgsql.Converters;

public static partial class NullableTimestampTzArrayConverter
{
    public static int GetByteCount(ReadOnlyMemory<DateTimeOffset?> value)
    {
        return BinaryNullableArray<DateTimeOffset, TimestampTzClrCodec>.Measure(value.Span, out _);
    }
    public static int Write(ReadOnlyMemory<DateTimeOffset?> value, Span<byte> destination)
    {
        return BinaryNullableArray<DateTimeOffset, TimestampTzClrCodec>.Write(value, destination);
    }
    public static void Write(ReadOnlyMemory<DateTimeOffset?> value, IBufferWriter<byte> destination)
    {
        BinaryNullableArray<DateTimeOffset, TimestampTzClrCodec>.Write(value, destination);
    }
    public static ReadOnlyMemory<DateTimeOffset?> ReadDateTimeOffsets(ReadOnlySpan<byte> payload)
    {
        return BinaryNullableArray<DateTimeOffset, TimestampTzClrCodec>.Read(payload);
    }
    public static ReadOnlyMemory<DateTimeOffset?> ReadDateTimeOffsets(ReadOnlySequence<byte> payload)
    {
        return BinaryNullableArray<DateTimeOffset, TimestampTzClrCodec>.Read(payload);
    }
    public static int Read(ReadOnlySpan<byte> payload, Span<DateTimeOffset?> destination)
    {
        return BinaryNullableArray<DateTimeOffset, TimestampTzClrCodec>.Read(payload, destination);
    }
    public static int Read(ReadOnlySequence<byte> payload, Span<DateTimeOffset?> destination)
    {
        return BinaryNullableArray<DateTimeOffset, TimestampTzClrCodec>.Read(payload, destination);
    }
}