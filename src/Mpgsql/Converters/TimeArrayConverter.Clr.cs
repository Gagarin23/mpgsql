using System.Buffers;

namespace Mpgsql.Converters;

public static partial class TimeArrayConverter
{
    public static int GetByteCount(ReadOnlyMemory<TimeOnly> value) => BinaryArray<TimeOnly, TimeClrCodec>.Measure(value.Span);
    public static int Write(ReadOnlyMemory<TimeOnly> value, Span<byte> destination) => BinaryArray<TimeOnly, TimeClrCodec>.Write(value, destination);
    public static void Write(ReadOnlyMemory<TimeOnly> value, IBufferWriter<byte> destination) => BinaryArray<TimeOnly, TimeClrCodec>.Write(value, destination);
    public static ReadOnlyMemory<TimeOnly> ReadTimeOnlys(ReadOnlySpan<byte> payload) => BinaryArray<TimeOnly, TimeClrCodec>.Read(payload);
    public static ReadOnlyMemory<TimeOnly> ReadTimeOnlys(ReadOnlySequence<byte> payload) => BinaryArray<TimeOnly, TimeClrCodec>.Read(payload);
    public static int Read(ReadOnlySpan<byte> payload, Span<TimeOnly> destination) => BinaryArray<TimeOnly, TimeClrCodec>.Read(payload, destination);
    public static int Read(ReadOnlySequence<byte> payload, Span<TimeOnly> destination) => BinaryArray<TimeOnly, TimeClrCodec>.Read(payload, destination);
}