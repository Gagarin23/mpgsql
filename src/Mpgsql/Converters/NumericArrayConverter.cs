using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Numeric[] for ReadOnlyMemory&lt;PgNumeric&gt;.</summary>
/// <remarks>
/// Supports empty and one-dimensional arrays; lower bounds are normalized to zero-based memory.
/// Writes only payload, with big-endian headers and element lengths. Capacity/overlap are checked
/// before writing. Read returns owned storage, or fills reusable storage without a payload copy.
/// An invalid element may leave reusable output partially written. SQL NULL is an outer field marker.
/// </remarks>
public static partial class NumericArrayConverter
{
    public const uint ElementTypeOid = (uint)TypeOid.Numeric;
    public const uint ArrayTypeOid = (uint)TypeOid.NumericArray;
    public static int GetByteCount(ReadOnlyMemory<PgNumeric> value) => BinaryArray<PgNumeric, NumericCodec>.Measure(value.Span);

    public static int Write(ReadOnlyMemory<PgNumeric> value, Span<byte> destination) => BinaryArray<PgNumeric, NumericCodec>.Write(value, destination);
    public static void Write(ReadOnlyMemory<PgNumeric> value, IBufferWriter<byte> destination) => BinaryArray<PgNumeric, NumericCodec>.Write(value, destination);
    public static ReadOnlyMemory<PgNumeric> Read(ReadOnlySpan<byte> payload) => BinaryArray<PgNumeric, NumericCodec>.Read(payload);
    public static ReadOnlyMemory<PgNumeric> Read(ReadOnlySequence<byte> payload) => BinaryArray<PgNumeric, NumericCodec>.Read(payload);
    public static int Read(ReadOnlySpan<byte> payload, Span<PgNumeric> destination) => BinaryArray<PgNumeric, NumericCodec>.Read(payload, destination);
    public static int Read(ReadOnlySequence<byte> payload, Span<PgNumeric> destination) => BinaryArray<PgNumeric, NumericCodec>.Read(payload, destination);
}