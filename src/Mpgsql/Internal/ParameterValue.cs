using Mpgsql.Converters;

namespace Mpgsql.Internal;

internal interface IParameterValue
{
    int Length { get; }
    void Write(Span<byte> destination);
}

internal sealed class ScalarParameterValue<T, TCodec>(T value) : IParameterValue
    where TCodec : struct, IBinaryCodec<T>
{
    public int Length => TCodec.Measure(value);
    public void Write(Span<byte> destination) => BinaryScalar<T, TCodec>.Write(value, destination);
}

internal sealed class ArrayParameterValue<T, TCodec>(ReadOnlyMemory<T> value) : IParameterValue
    where TCodec : struct, IBinaryCodec<T>
{
    public int Length => BinaryArray<T, TCodec>.Measure(value.Span);
    public void Write(Span<byte> destination) => BinaryArray<T, TCodec>.Write(value, destination);
}

internal sealed class NullableArrayParameterValue<T, TCodec>(ReadOnlyMemory<T?> value) : IParameterValue
    where T : struct
    where TCodec : struct, IBinaryCodec<T>
{
    public int Length => BinaryNullableArray<T, TCodec>.Measure(value.Span, out _);
    public void Write(Span<byte> destination) => BinaryNullableArray<T, TCodec>.Write(value, destination);
}

internal sealed class ReferenceArrayParameterValue<T, TCodec>(ReadOnlyMemory<T?> value) : IParameterValue
    where T : class
    where TCodec : struct, IBinaryCodec<T>
{
    public int Length => BinaryReferenceArray<T, TCodec>.Measure(value.Span, out _);
    public void Write(Span<byte> destination) => BinaryReferenceArray<T, TCodec>.Write(value, destination);
}
