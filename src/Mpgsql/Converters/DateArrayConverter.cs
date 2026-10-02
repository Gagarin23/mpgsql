using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Date[] for ReadOnlyMemory&lt;PgDate&gt;.</summary>
/// <remarks>
/// Supports empty and one-dimensional arrays; lower bounds are normalized to zero-based memory.
/// Writes only payload, with big-endian headers and element lengths. Capacity/overlap are checked
/// before writing. Read returns owned storage, or fills reusable storage without a payload copy.
/// An invalid element may leave reusable output partially written. SQL NULL is an outer field marker.
/// </remarks>
public static partial class DateArrayConverter
{
    public const uint ElementTypeOid = (uint)TypeOid.Date;
    public const uint ArrayTypeOid = (uint)TypeOid.DateArray;
    public static int GetByteCount(ReadOnlyMemory<PgDate> value) => BinaryArray<PgDate, DateCodec>.Measure(value.Span);
    public static int GetByteCount(int elementCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elementCount);
        return ArrayPayload.Measure(elementCount, checked(elementCount * 4));
    }
    public static int Write(ReadOnlyMemory<PgDate> value, Span<byte> destination) => BinaryArray<PgDate, DateCodec>.Write(value, destination);
    public static void Write(ReadOnlyMemory<PgDate> value, IBufferWriter<byte> destination) => BinaryArray<PgDate, DateCodec>.Write(value, destination);
    public static ReadOnlyMemory<PgDate> Read(ReadOnlySpan<byte> payload) => BinaryArray<PgDate, DateCodec>.Read(payload);
    public static ReadOnlyMemory<PgDate> Read(ReadOnlySequence<byte> payload) => BinaryArray<PgDate, DateCodec>.Read(payload);
    public static int Read(ReadOnlySpan<byte> payload, Span<PgDate> destination) => BinaryArray<PgDate, DateCodec>.Read(payload, destination);
    public static int Read(ReadOnlySequence<byte> payload, Span<PgDate> destination) => BinaryArray<PgDate, DateCodec>.Read(payload, destination);
}