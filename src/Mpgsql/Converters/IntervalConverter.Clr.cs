using System.Buffers;

namespace Mpgsql.Converters;

public static partial class IntervalConverter
{
    public static int GetByteCount(TimeSpan value) => IntervalClrCodec.Measure(value);
    public static int GetByteCount(TimeSpan? value) => value.HasValue ? GetByteCount(value.Value) : 0;
    public static int Write(TimeSpan value, Span<byte> destination) => BinaryScalar<TimeSpan, IntervalClrCodec>.Write(value, destination);
    public static int Write(TimeSpan? value, Span<byte> destination) => value.HasValue ? Write(value.Value, destination) : 0;
    public static void Write(TimeSpan value, IBufferWriter<byte> destination) => BinaryScalar<TimeSpan, IntervalClrCodec>.Write(value, destination);
    public static void Write(TimeSpan? value, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (value.HasValue)
        {
            Write(value.Value, destination);
        }
    }
    public static TimeSpan ReadTimeSpan(ReadOnlySpan<byte> payload) => IntervalClrCodec.Read(payload);
    public static TimeSpan ReadTimeSpan(ReadOnlySequence<byte> payload) => IntervalClrCodec.Read(payload);
    public static TimeSpan? ReadNullableTimeSpan(ReadOnlyMemory<byte>? payload) => payload is { } value ? ReadTimeSpan(value.Span) : null;
    public static TimeSpan? ReadNullableTimeSpan(ReadOnlySequence<byte>? payload) => payload is { } value ? ReadTimeSpan(value) : null;
}