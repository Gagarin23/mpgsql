using System.Buffers;

namespace Mpgsql.Converters;

public static partial class DateArrayConverter
{
    public static int GetByteCount(ReadOnlyMemory<DateOnly> value) => BinaryArray<DateOnly, DateClrCodec>.Measure(value.Span);
    public static int Write(ReadOnlyMemory<DateOnly> value, Span<byte> destination) => BinaryArray<DateOnly, DateClrCodec>.Write(value, destination);
    public static void Write(ReadOnlyMemory<DateOnly> value, IBufferWriter<byte> destination) => BinaryArray<DateOnly, DateClrCodec>.Write(value, destination);
    public static ReadOnlyMemory<DateOnly> ReadDateOnlys(ReadOnlySpan<byte> payload) => BinaryArray<DateOnly, DateClrCodec>.Read(payload);
    public static ReadOnlyMemory<DateOnly> ReadDateOnlys(ReadOnlySequence<byte> payload) => BinaryArray<DateOnly, DateClrCodec>.Read(payload);
    public static int Read(ReadOnlySpan<byte> payload, Span<DateOnly> destination) => BinaryArray<DateOnly, DateClrCodec>.Read(payload, destination);
    public static int Read(ReadOnlySequence<byte> payload, Span<DateOnly> destination) => BinaryArray<DateOnly, DateClrCodec>.Read(payload, destination);
}