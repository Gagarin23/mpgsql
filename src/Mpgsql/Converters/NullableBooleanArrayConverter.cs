using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Boolean[] with NULL elements for ReadOnlyMemory&lt;bool?&gt;.</summary>
/// <remarks>Same framing/buffer contract as BooleanArrayConverter; NULL elements have length -1 and no payload.</remarks>
public static partial class NullableBooleanArrayConverter
{
    public const uint ElementTypeOid = (uint)TypeOid.Boolean;
    public const uint ArrayTypeOid = (uint)TypeOid.BooleanArray;
    public static int GetByteCount(ReadOnlyMemory<bool?> value) => BinaryNullableArray<bool, BooleanCodec>.Measure(value.Span, out _);
    public static int GetByteCount(int elementCount, int nullCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elementCount);
        ArgumentOutOfRangeException.ThrowIfNegative(nullCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(nullCount, elementCount);
        return ArrayPayload.Measure(elementCount, checked((elementCount - nullCount) * 1));
    }
    public static int Write(ReadOnlyMemory<bool?> value, Span<byte> destination) => BinaryNullableArray<bool, BooleanCodec>.Write(value, destination);
    public static void Write(ReadOnlyMemory<bool?> value, IBufferWriter<byte> destination) => BinaryNullableArray<bool, BooleanCodec>.Write(value, destination);
    public static ReadOnlyMemory<bool?> Read(ReadOnlySpan<byte> payload) => BinaryNullableArray<bool, BooleanCodec>.Read(payload);
    public static ReadOnlyMemory<bool?> Read(ReadOnlySequence<byte> payload) => BinaryNullableArray<bool, BooleanCodec>.Read(payload);
    public static int Read(ReadOnlySpan<byte> payload, Span<bool?> destination) => BinaryNullableArray<bool, BooleanCodec>.Read(payload, destination);
    public static int Read(ReadOnlySequence<byte> payload, Span<bool?> destination) => BinaryNullableArray<bool, BooleanCodec>.Read(payload, destination);
}