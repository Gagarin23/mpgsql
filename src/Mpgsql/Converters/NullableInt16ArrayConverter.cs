using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Int16[] with NULL elements for ReadOnlyMemory&lt;short?&gt;.</summary>
/// <remarks>Same framing/buffer contract as Int16ArrayConverter; NULL elements have length -1 and no payload.</remarks>
public static partial class NullableInt16ArrayConverter
{
    public const uint ElementTypeOid = (uint)TypeOid.Int16;
    public const uint ArrayTypeOid = (uint)TypeOid.Int16Array;
    public static int GetByteCount(ReadOnlyMemory<short?> value) => BinaryNullableArray<short, Int16Codec>.Measure(value.Span, out _);
    public static int GetByteCount(int elementCount, int nullCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elementCount);
        ArgumentOutOfRangeException.ThrowIfNegative(nullCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(nullCount, elementCount);
        return ArrayPayload.Measure(elementCount, checked((elementCount - nullCount) * 2));
    }
    public static int Write(ReadOnlyMemory<short?> value, Span<byte> destination) => BinaryNullableArray<short, Int16Codec>.Write(value, destination);
    public static void Write(ReadOnlyMemory<short?> value, IBufferWriter<byte> destination) => BinaryNullableArray<short, Int16Codec>.Write(value, destination);
    public static ReadOnlyMemory<short?> Read(ReadOnlySpan<byte> payload) => BinaryNullableArray<short, Int16Codec>.Read(payload);
    public static ReadOnlyMemory<short?> Read(ReadOnlySequence<byte> payload) => BinaryNullableArray<short, Int16Codec>.Read(payload);
    public static int Read(ReadOnlySpan<byte> payload, Span<short?> destination) => BinaryNullableArray<short, Int16Codec>.Read(payload, destination);
    public static int Read(ReadOnlySequence<byte> payload, Span<short?> destination) => BinaryNullableArray<short, Int16Codec>.Read(payload, destination);
}