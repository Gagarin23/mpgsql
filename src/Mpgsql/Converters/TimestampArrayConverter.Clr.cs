using System.Buffers;

namespace Mpgsql.Converters;

public static partial class TimestampArrayConverter
{
    public static int GetByteCount(ReadOnlyMemory<DateTime> value) => BinaryArray<DateTime, TimestampClrCodec>.Measure(value.Span);
    public static int Write(ReadOnlyMemory<DateTime> value, Span<byte> destination) => BinaryArray<DateTime, TimestampClrCodec>.Write(value, destination);
    public static void Write(ReadOnlyMemory<DateTime> value, IBufferWriter<byte> destination) => BinaryArray<DateTime, TimestampClrCodec>.Write(value, destination);
    public static ReadOnlyMemory<DateTime> ReadDateTimes(ReadOnlySpan<byte> payload) => BinaryArray<DateTime, TimestampClrCodec>.Read(payload);
    public static ReadOnlyMemory<DateTime> ReadDateTimes(ReadOnlySequence<byte> payload) => BinaryArray<DateTime, TimestampClrCodec>.Read(payload);
    public static int Read(ReadOnlySpan<byte> payload, Span<DateTime> destination) => BinaryArray<DateTime, TimestampClrCodec>.Read(payload, destination);
    public static int Read(ReadOnlySequence<byte> payload, Span<DateTime> destination) => BinaryArray<DateTime, TimestampClrCodec>.Read(payload, destination);
}