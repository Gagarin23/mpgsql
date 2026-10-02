using System.Buffers;

namespace Mpgsql.Converters;

public static partial class TimestampConverter
{
    public static int GetByteCount(DateTime value) => TimestampClrCodec.Measure(value);
    public static int GetByteCount(DateTime? value) => value.HasValue ? GetByteCount(value.Value) : 0;
    public static int Write(DateTime value, Span<byte> destination) => BinaryScalar<DateTime, TimestampClrCodec>.Write(value, destination);
    public static int Write(DateTime? value, Span<byte> destination) => value.HasValue ? Write(value.Value, destination) : 0;
    public static void Write(DateTime value, IBufferWriter<byte> destination) => BinaryScalar<DateTime, TimestampClrCodec>.Write(value, destination);
    public static void Write(DateTime? value, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (value.HasValue)
        {
            Write(value.Value, destination);
        }
    }
    public static DateTime ReadDateTime(ReadOnlySpan<byte> payload) => TimestampClrCodec.Read(payload);
    public static DateTime ReadDateTime(ReadOnlySequence<byte> payload) => TimestampClrCodec.Read(payload);
    public static DateTime? ReadNullableDateTime(ReadOnlyMemory<byte>? payload) => payload is { } value ? ReadDateTime(value.Span) : null;
    public static DateTime? ReadNullableDateTime(ReadOnlySequence<byte>? payload) => payload is { } value ? ReadDateTime(value) : null;
}