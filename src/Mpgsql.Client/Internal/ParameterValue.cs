using Mpgsql.Converters;

namespace Mpgsql.Internal;

internal interface IParameterValue
{
    int Length { get; }
    int Write(Span<byte> destination);
}

internal sealed class ScalarParameterValue<T, TCodec>(T value) : IParameterValue
    where TCodec : struct, IBinaryCodec<T>
{
    public int Length => TCodec.Measure(value);
    public int Write(Span<byte> destination)
    {
        // QueryPacket has measured the value and checked full capacity before writing.
        TCodec.CheckOverlap(value, destination);
        return TCodec.Write(value, destination);
    }
}

internal sealed class ArrayParameterValue<T, TCodec>(ReadOnlyMemory<T> value) : IParameterValue
    where TCodec : struct, IBinaryCodec<T>
{
    public int Length => BinaryArray<T, TCodec>.Measure(value.Span);
    public int Write(Span<byte> destination) => BinaryArray<T, TCodec>.WriteMeasured(value, destination);
}

internal sealed class NullableArrayParameterValue<T, TCodec>(ReadOnlyMemory<T?> value) : IParameterValue
    where T : struct
    where TCodec : struct, IBinaryCodec<T>
{
    public int Length => BinaryNullableArray<T, TCodec>.Measure(value.Span, out _);
    public int Write(Span<byte> destination) => BinaryNullableArray<T, TCodec>.WriteMeasured(value, destination);
}

internal sealed class ReferenceArrayParameterValue<T, TCodec>(ReadOnlyMemory<T?> value) : IParameterValue
    where T : class
    where TCodec : struct, IBinaryCodec<T>
{
    public int Length => BinaryReferenceArray<T, TCodec>.Measure(value.Span, out _);
    public int Write(Span<byte> destination) => BinaryReferenceArray<T, TCodec>.WriteMeasured(value, destination);
}
