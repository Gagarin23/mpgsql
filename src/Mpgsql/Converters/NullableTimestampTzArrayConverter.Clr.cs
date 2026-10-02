using System.Buffers;

namespace Mpgsql.Converters;

public static partial class NullableTimestampTzArrayConverter
{
    public static int GetByteCount(ReadOnlyMemory<DateTimeOffset?> value) => BinaryNullableArray<DateTimeOffset, TimestampTzClrCodec>.Measure(value.Span, out _);
    public static int Write(ReadOnlyMemory<DateTimeOffset?> value, Span<byte> destination) => BinaryNullableArray<DateTimeOffset, TimestampTzClrCodec>.Write(value, destination);
    public static void Write(ReadOnlyMemory<DateTimeOffset?> value, IBufferWriter<byte> destination) => BinaryNullableArray<DateTimeOffset, TimestampTzClrCodec>.Write(value, destination);
    public static ReadOnlyMemory<DateTimeOffset?> ReadDateTimeOffsets(ReadOnlySpan<byte> payload) => BinaryNullableArray<DateTimeOffset, TimestampTzClrCodec>.Read(payload);
    public static ReadOnlyMemory<DateTimeOffset?> ReadDateTimeOffsets(ReadOnlySequence<byte> payload) => BinaryNullableArray<DateTimeOffset, TimestampTzClrCodec>.Read(payload);
    public static int Read(ReadOnlySpan<byte> payload, Span<DateTimeOffset?> destination) => BinaryNullableArray<DateTimeOffset, TimestampTzClrCodec>.Read(payload, destination);
    public static int Read(ReadOnlySequence<byte> payload, Span<DateTimeOffset?> destination) => BinaryNullableArray<DateTimeOffset, TimestampTzClrCodec>.Read(payload, destination);
}