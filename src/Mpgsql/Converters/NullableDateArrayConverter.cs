using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Date[] with NULL elements for ReadOnlyMemory&lt;PgDate?&gt;.</summary>
/// <remarks>Same framing/buffer contract as DateArrayConverter; NULL elements have length -1 and no payload.</remarks>
public static partial class NullableDateArrayConverter
{
    public const uint ElementTypeOid = (uint)TypeOid.Date;
    public const uint ArrayTypeOid = (uint)TypeOid.DateArray;
    public static int GetByteCount(ReadOnlyMemory<PgDate?> value) => BinaryNullableArray<PgDate, DateCodec>.Measure(value.Span, out _);
    public static int GetByteCount(int elementCount, int nullCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elementCount);
        ArgumentOutOfRangeException.ThrowIfNegative(nullCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(nullCount, elementCount);
        return ArrayPayload.Measure(elementCount, checked((elementCount - nullCount) * 4));
    }
    public static int Write(ReadOnlyMemory<PgDate?> value, Span<byte> destination) => BinaryNullableArray<PgDate, DateCodec>.Write(value, destination);
    public static void Write(ReadOnlyMemory<PgDate?> value, IBufferWriter<byte> destination) => BinaryNullableArray<PgDate, DateCodec>.Write(value, destination);
    public static ReadOnlyMemory<PgDate?> Read(ReadOnlySpan<byte> payload) => BinaryNullableArray<PgDate, DateCodec>.Read(payload);
    public static ReadOnlyMemory<PgDate?> Read(ReadOnlySequence<byte> payload) => BinaryNullableArray<PgDate, DateCodec>.Read(payload);
    public static int Read(ReadOnlySpan<byte> payload, Span<PgDate?> destination) => BinaryNullableArray<PgDate, DateCodec>.Read(payload, destination);
    public static int Read(ReadOnlySequence<byte> payload, Span<PgDate?> destination) => BinaryNullableArray<PgDate, DateCodec>.Read(payload, destination);
}