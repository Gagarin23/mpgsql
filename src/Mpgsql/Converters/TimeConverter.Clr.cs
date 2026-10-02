using System.Buffers;

namespace Mpgsql.Converters;

public static partial class TimeConverter
{
    public static int GetByteCount(TimeOnly value) => TimeClrCodec.Measure(value);
    public static int GetByteCount(TimeOnly? value) => value.HasValue ? GetByteCount(value.Value) : 0;
    public static int Write(TimeOnly value, Span<byte> destination) => BinaryScalar<TimeOnly, TimeClrCodec>.Write(value, destination);
    public static int Write(TimeOnly? value, Span<byte> destination) => value.HasValue ? Write(value.Value, destination) : 0;
    public static void Write(TimeOnly value, IBufferWriter<byte> destination) => BinaryScalar<TimeOnly, TimeClrCodec>.Write(value, destination);
    public static void Write(TimeOnly? value, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (value.HasValue)
        {
            Write(value.Value, destination);
        }
    }
    public static TimeOnly ReadTimeOnly(ReadOnlySpan<byte> payload) => TimeClrCodec.Read(payload);
    public static TimeOnly ReadTimeOnly(ReadOnlySequence<byte> payload) => TimeClrCodec.Read(payload);
    public static TimeOnly? ReadNullableTimeOnly(ReadOnlyMemory<byte>? payload) => payload is { } value ? ReadTimeOnly(value.Span) : null;
    public static TimeOnly? ReadNullableTimeOnly(ReadOnlySequence<byte>? payload) => payload is { } value ? ReadTimeOnly(value) : null;
}