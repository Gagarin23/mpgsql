using System.Buffers;

namespace Mpgsql.Converters;

public static partial class TimestampTzConverter
{
    public static int GetByteCount(DateTimeOffset value)
    {
        return TimestampTzClrCodec.Measure(value);
    }
    public static int GetByteCount(DateTimeOffset? value)
    {
        return value.HasValue ? GetByteCount(value.Value) : 0;
    }
    public static int Write(DateTimeOffset value, Span<byte> destination)
    {
        return BinaryScalar<DateTimeOffset, TimestampTzClrCodec>.Write(value, destination);
    }
    public static int Write(DateTimeOffset? value, Span<byte> destination)
    {
        return value.HasValue ? Write(value.Value, destination) : 0;
    }
    public static void Write(DateTimeOffset value, IBufferWriter<byte> destination)
    {
        BinaryScalar<DateTimeOffset, TimestampTzClrCodec>.Write(value, destination);
    }
    public static void Write(DateTimeOffset? value, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (value.HasValue)
        {
            Write(value.Value, destination);
        }
    }
    public static DateTimeOffset ReadDateTimeOffset(ReadOnlySpan<byte> payload)
    {
        return TimestampTzClrCodec.Read(payload);
    }
    public static DateTimeOffset ReadDateTimeOffset(ReadOnlySequence<byte> payload)
    {
        return TimestampTzClrCodec.Read(payload);
    }
    public static DateTimeOffset? ReadNullableDateTimeOffset(ReadOnlyMemory<byte>? payload)
    {
        return payload is { } value ? ReadDateTimeOffset(value.Span) : null;
    }
    public static DateTimeOffset? ReadNullableDateTimeOffset(ReadOnlySequence<byte>? payload)
    {
        return payload is { } value ? ReadDateTimeOffset(value) : null;
    }
}