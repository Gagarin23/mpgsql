using System.Buffers;

namespace Mpgsql.Converters;

public static partial class NullableTimeArrayConverter
{
    public static int GetByteCount(ReadOnlyMemory<TimeOnly?> value) => BinaryNullableArray<TimeOnly, TimeClrCodec>.Measure(value.Span, out _);
    public static int Write(ReadOnlyMemory<TimeOnly?> value, Span<byte> destination) => BinaryNullableArray<TimeOnly, TimeClrCodec>.Write(value, destination);
    public static void Write(ReadOnlyMemory<TimeOnly?> value, IBufferWriter<byte> destination) => BinaryNullableArray<TimeOnly, TimeClrCodec>.Write(value, destination);
    public static ReadOnlyMemory<TimeOnly?> ReadTimeOnlys(ReadOnlySpan<byte> payload) => BinaryNullableArray<TimeOnly, TimeClrCodec>.Read(payload);
    public static ReadOnlyMemory<TimeOnly?> ReadTimeOnlys(ReadOnlySequence<byte> payload) => BinaryNullableArray<TimeOnly, TimeClrCodec>.Read(payload);
    public static int Read(ReadOnlySpan<byte> payload, Span<TimeOnly?> destination) => BinaryNullableArray<TimeOnly, TimeClrCodec>.Read(payload, destination);
    public static int Read(ReadOnlySequence<byte> payload, Span<TimeOnly?> destination) => BinaryNullableArray<TimeOnly, TimeClrCodec>.Read(payload, destination);
}