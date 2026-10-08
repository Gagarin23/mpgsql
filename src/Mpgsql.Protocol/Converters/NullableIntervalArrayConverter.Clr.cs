using System.Buffers;

namespace Mpgsql.Converters;

public static partial class NullableIntervalArrayConverter
{
    public static int GetByteCount(ReadOnlyMemory<TimeSpan?> value)
    {
        return BinaryNullableArray<TimeSpan, IntervalClrCodec>.Measure(value.Span, out _);
    }
    public static int Write(ReadOnlyMemory<TimeSpan?> value, Span<byte> destination)
    {
        return BinaryNullableArray<TimeSpan, IntervalClrCodec>.Write(value, destination);
    }
    public static void Write(ReadOnlyMemory<TimeSpan?> value, IBufferWriter<byte> destination)
    {
        BinaryNullableArray<TimeSpan, IntervalClrCodec>.Write(value, destination);
    }
    public static ReadOnlyMemory<TimeSpan?> ReadTimeSpans(ReadOnlySpan<byte> payload)
    {
        return BinaryNullableArray<TimeSpan, IntervalClrCodec>.Read(payload);
    }
    public static ReadOnlyMemory<TimeSpan?> ReadTimeSpans(ReadOnlySequence<byte> payload)
    {
        return BinaryNullableArray<TimeSpan, IntervalClrCodec>.Read(payload);
    }
    public static int Read(ReadOnlySpan<byte> payload, Span<TimeSpan?> destination)
    {
        return BinaryNullableArray<TimeSpan, IntervalClrCodec>.Read(payload, destination);
    }
    public static int Read(ReadOnlySequence<byte> payload, Span<TimeSpan?> destination)
    {
        return BinaryNullableArray<TimeSpan, IntervalClrCodec>.Read(payload, destination);
    }
}