using System.Buffers;

namespace Mpgsql.Converters;

public static partial class NullableTimestampArrayConverter
{
    public static int GetByteCount(ReadOnlyMemory<DateTime?> value)
    {
        return BinaryNullableArray<DateTime, TimestampClrCodec>.Measure(value.Span, out _);
    }
    public static int Write(ReadOnlyMemory<DateTime?> value, Span<byte> destination)
    {
        return BinaryNullableArray<DateTime, TimestampClrCodec>.Write(value, destination);
    }
    public static void Write(ReadOnlyMemory<DateTime?> value, IBufferWriter<byte> destination)
    {
        BinaryNullableArray<DateTime, TimestampClrCodec>.Write(value, destination);
    }
    public static ReadOnlyMemory<DateTime?> ReadDateTimes(ReadOnlySpan<byte> payload)
    {
        return BinaryNullableArray<DateTime, TimestampClrCodec>.Read(payload);
    }
    public static ReadOnlyMemory<DateTime?> ReadDateTimes(ReadOnlySequence<byte> payload)
    {
        return BinaryNullableArray<DateTime, TimestampClrCodec>.Read(payload);
    }
    public static int Read(ReadOnlySpan<byte> payload, Span<DateTime?> destination)
    {
        return BinaryNullableArray<DateTime, TimestampClrCodec>.Read(payload, destination);
    }
    public static int Read(ReadOnlySequence<byte> payload, Span<DateTime?> destination)
    {
        return BinaryNullableArray<DateTime, TimestampClrCodec>.Read(payload, destination);
    }
}