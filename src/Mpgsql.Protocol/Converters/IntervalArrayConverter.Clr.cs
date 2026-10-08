using System.Buffers;

namespace Mpgsql.Converters;

public static partial class IntervalArrayConverter
{
    public static int GetByteCount(ReadOnlyMemory<TimeSpan> value)
    {
        return BinaryArray<TimeSpan, IntervalClrCodec>.Measure(value.Span);
    }
    public static int Write(ReadOnlyMemory<TimeSpan> value, Span<byte> destination)
    {
        return BinaryArray<TimeSpan, IntervalClrCodec>.Write(value, destination);
    }
    public static void Write(ReadOnlyMemory<TimeSpan> value, IBufferWriter<byte> destination)
    {
        BinaryArray<TimeSpan, IntervalClrCodec>.Write(value, destination);
    }
    public static ReadOnlyMemory<TimeSpan> ReadTimeSpans(ReadOnlySpan<byte> payload)
    {
        return BinaryArray<TimeSpan, IntervalClrCodec>.Read(payload);
    }
    public static ReadOnlyMemory<TimeSpan> ReadTimeSpans(ReadOnlySequence<byte> payload)
    {
        return BinaryArray<TimeSpan, IntervalClrCodec>.Read(payload);
    }
    public static int Read(ReadOnlySpan<byte> payload, Span<TimeSpan> destination)
    {
        return BinaryArray<TimeSpan, IntervalClrCodec>.Read(payload, destination);
    }
    public static int Read(ReadOnlySequence<byte> payload, Span<TimeSpan> destination)
    {
        return BinaryArray<TimeSpan, IntervalClrCodec>.Read(payload, destination);
    }
}