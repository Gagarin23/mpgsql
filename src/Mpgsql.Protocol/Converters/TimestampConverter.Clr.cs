using System.Buffers;

namespace Mpgsql.Converters;

public static partial class TimestampConverter
{
    public static int GetByteCount(DateTime value)
    {
        return TimestampClrCodec.Measure(value);
    }
    public static int GetByteCount(DateTime? value)
    {
        return value.HasValue ? GetByteCount(value.Value) : 0;
    }
    public static int Write(DateTime value, Span<byte> destination)
    {
        return BinaryScalar<DateTime, TimestampClrCodec>.Write(value, destination);
    }
    public static int Write(DateTime? value, Span<byte> destination)
    {
        return value.HasValue ? Write(value.Value, destination) : 0;
    }
    public static void Write(DateTime value, IBufferWriter<byte> destination)
    {
        BinaryScalar<DateTime, TimestampClrCodec>.Write(value, destination);
    }
    public static void Write(DateTime? value, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (value.HasValue)
        {
            Write(value.Value, destination);
        }
    }
    public static DateTime ReadDateTime(ReadOnlySpan<byte> payload)
    {
        return TimestampClrCodec.Read(payload);
    }
    public static DateTime ReadDateTime(ReadOnlySequence<byte> payload)
    {
        return TimestampClrCodec.Read(payload);
    }
    public static DateTime? ReadNullableDateTime(ReadOnlyMemory<byte>? payload)
    {
        return payload is { } value ? ReadDateTime(value.Span) : null;
    }
    public static DateTime? ReadNullableDateTime(ReadOnlySequence<byte>? payload)
    {
        return payload is { } value ? ReadDateTime(value) : null;
    }
}