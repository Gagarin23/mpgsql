using System.Buffers;

namespace Mpgsql.Converters;

public static partial class TimestampTzConverter
{
    public static int GetByteCount(DateTimeOffset value) => TimestampTzClrCodec.Measure(value);
    public static int GetByteCount(DateTimeOffset? value) => value.HasValue ? GetByteCount(value.Value) : 0;
    public static int Write(DateTimeOffset value, Span<byte> destination) => BinaryScalar<DateTimeOffset, TimestampTzClrCodec>.Write(value, destination);
    public static int Write(DateTimeOffset? value, Span<byte> destination) => value.HasValue ? Write(value.Value, destination) : 0;
    public static void Write(DateTimeOffset value, IBufferWriter<byte> destination) => BinaryScalar<DateTimeOffset, TimestampTzClrCodec>.Write(value, destination);
    public static void Write(DateTimeOffset? value, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (value.HasValue)
        {
            Write(value.Value, destination);
        }
    }
    public static DateTimeOffset ReadDateTimeOffset(ReadOnlySpan<byte> payload) => TimestampTzClrCodec.Read(payload);
    public static DateTimeOffset ReadDateTimeOffset(ReadOnlySequence<byte> payload) => TimestampTzClrCodec.Read(payload);
    public static DateTimeOffset? ReadNullableDateTimeOffset(ReadOnlyMemory<byte>? payload) => payload is { } value ? ReadDateTimeOffset(value.Span) : null;
    public static DateTimeOffset? ReadNullableDateTimeOffset(ReadOnlySequence<byte>? payload) => payload is { } value ? ReadDateTimeOffset(value) : null;
}