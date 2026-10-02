using System.Buffers;

namespace Mpgsql.Converters;

public static partial class DateConverter
{
    public static int GetByteCount(DateOnly value) => DateClrCodec.Measure(value);
    public static int GetByteCount(DateOnly? value) => value.HasValue ? GetByteCount(value.Value) : 0;
    public static int Write(DateOnly value, Span<byte> destination) => BinaryScalar<DateOnly, DateClrCodec>.Write(value, destination);
    public static int Write(DateOnly? value, Span<byte> destination) => value.HasValue ? Write(value.Value, destination) : 0;
    public static void Write(DateOnly value, IBufferWriter<byte> destination) => BinaryScalar<DateOnly, DateClrCodec>.Write(value, destination);
    public static void Write(DateOnly? value, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (value.HasValue)
        {
            Write(value.Value, destination);
        }
    }
    public static DateOnly ReadDateOnly(ReadOnlySpan<byte> payload) => DateClrCodec.Read(payload);
    public static DateOnly ReadDateOnly(ReadOnlySequence<byte> payload) => DateClrCodec.Read(payload);
    public static DateOnly? ReadNullableDateOnly(ReadOnlyMemory<byte>? payload) => payload is { } value ? ReadDateOnly(value.Span) : null;
    public static DateOnly? ReadNullableDateOnly(ReadOnlySequence<byte>? payload) => payload is { } value ? ReadDateOnly(value) : null;
}