using System.Buffers;

namespace Mpgsql.Converters;

public static partial class TimestampTzArrayConverter
{
    public static int GetByteCount(ReadOnlyMemory<DateTimeOffset> value) => BinaryArray<DateTimeOffset, TimestampTzClrCodec>.Measure(value.Span);
    public static int Write(ReadOnlyMemory<DateTimeOffset> value, Span<byte> destination) => BinaryArray<DateTimeOffset, TimestampTzClrCodec>.Write(value, destination);
    public static void Write(ReadOnlyMemory<DateTimeOffset> value, IBufferWriter<byte> destination) => BinaryArray<DateTimeOffset, TimestampTzClrCodec>.Write(value, destination);
    public static ReadOnlyMemory<DateTimeOffset> ReadDateTimeOffsets(ReadOnlySpan<byte> payload) => BinaryArray<DateTimeOffset, TimestampTzClrCodec>.Read(payload);
    public static ReadOnlyMemory<DateTimeOffset> ReadDateTimeOffsets(ReadOnlySequence<byte> payload) => BinaryArray<DateTimeOffset, TimestampTzClrCodec>.Read(payload);
    public static int Read(ReadOnlySpan<byte> payload, Span<DateTimeOffset> destination) => BinaryArray<DateTimeOffset, TimestampTzClrCodec>.Read(payload, destination);
    public static int Read(ReadOnlySequence<byte> payload, Span<DateTimeOffset> destination) => BinaryArray<DateTimeOffset, TimestampTzClrCodec>.Read(payload, destination);
}