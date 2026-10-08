using System.Buffers;

namespace Mpgsql.Converters;

public static partial class IntervalConverter
{
    public static int GetByteCount(TimeSpan value)
    {
        return IntervalClrCodec.Measure(value);
    }
    public static int GetByteCount(TimeSpan? value)
    {
        return value.HasValue ? GetByteCount(value.Value) : 0;
    }
    public static int Write(TimeSpan value, Span<byte> destination)
    {
        return BinaryScalar<TimeSpan, IntervalClrCodec>.Write(value, destination);
    }
    public static int Write(TimeSpan? value, Span<byte> destination)
    {
        return value.HasValue ? Write(value.Value, destination) : 0;
    }
    public static void Write(TimeSpan value, IBufferWriter<byte> destination)
    {
        BinaryScalar<TimeSpan, IntervalClrCodec>.Write(value, destination);
    }
    public static void Write(TimeSpan? value, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (value.HasValue)
        {
            Write(value.Value, destination);
        }
    }
    public static TimeSpan ReadTimeSpan(ReadOnlySpan<byte> payload)
    {
        return IntervalClrCodec.Read(payload);
    }
    public static TimeSpan ReadTimeSpan(ReadOnlySequence<byte> payload)
    {
        return IntervalClrCodec.Read(payload);
    }
    public static TimeSpan? ReadNullableTimeSpan(ReadOnlyMemory<byte>? payload)
    {
        return payload is { } value ? ReadTimeSpan(value.Span) : null;
    }
    public static TimeSpan? ReadNullableTimeSpan(ReadOnlySequence<byte>? payload)
    {
        return payload is { } value ? ReadTimeSpan(value) : null;
    }
}