using System.Buffers;

namespace Mpgsql.Converters;

public static partial class NullableNumericArrayConverter
{
    public static int GetByteCount(ReadOnlyMemory<decimal?> value) => BinaryNullableArray<decimal, DecimalCodec>.Measure(value.Span, out _);
    public static int Write(ReadOnlyMemory<decimal?> value, Span<byte> destination) => BinaryNullableArray<decimal, DecimalCodec>.Write(value, destination);
    public static void Write(ReadOnlyMemory<decimal?> value, IBufferWriter<byte> destination) => BinaryNullableArray<decimal, DecimalCodec>.Write(value, destination);
    public static ReadOnlyMemory<decimal?> ReadDecimals(ReadOnlySpan<byte> payload) => BinaryNullableArray<decimal, DecimalCodec>.Read(payload);
    public static ReadOnlyMemory<decimal?> ReadDecimals(ReadOnlySequence<byte> payload) => BinaryNullableArray<decimal, DecimalCodec>.Read(payload);
    public static int Read(ReadOnlySpan<byte> payload, Span<decimal?> destination) => BinaryNullableArray<decimal, DecimalCodec>.Read(payload, destination);
    public static int Read(ReadOnlySequence<byte> payload, Span<decimal?> destination) => BinaryNullableArray<decimal, DecimalCodec>.Read(payload, destination);
}