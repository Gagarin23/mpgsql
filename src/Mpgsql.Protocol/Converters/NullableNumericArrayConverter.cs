using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Numeric[] with NULL elements for ReadOnlyMemory&lt;PgNumeric?&gt;.</summary>
/// <remarks>Same framing/buffer contract as NumericArrayConverter; NULL elements have length -1 and no payload.</remarks>
public static partial class NullableNumericArrayConverter
{
    public const uint ElementTypeOid = (uint)TypeOid.Numeric;
    public const uint ArrayTypeOid = (uint)TypeOid.NumericArray;
    public static int GetByteCount(ReadOnlyMemory<PgNumeric?> value)
    {
        return BinaryNullableArray<PgNumeric, NumericCodec>.Measure(value.Span, out _);
    }

    public static int Write(ReadOnlyMemory<PgNumeric?> value, Span<byte> destination)
    {
        return BinaryNullableArray<PgNumeric, NumericCodec>.Write(value, destination);
    }
    public static void Write(ReadOnlyMemory<PgNumeric?> value, IBufferWriter<byte> destination)
    {
        BinaryNullableArray<PgNumeric, NumericCodec>.Write(value, destination);
    }
    public static ReadOnlyMemory<PgNumeric?> Read(ReadOnlySpan<byte> payload)
    {
        return BinaryNullableArray<PgNumeric, NumericCodec>.Read(payload);
    }
    public static ReadOnlyMemory<PgNumeric?> Read(ReadOnlySequence<byte> payload)
    {
        return BinaryNullableArray<PgNumeric, NumericCodec>.Read(payload);
    }
    public static int Read(ReadOnlySpan<byte> payload, Span<PgNumeric?> destination)
    {
        return BinaryNullableArray<PgNumeric, NumericCodec>.Read(payload, destination);
    }
    public static int Read(ReadOnlySequence<byte> payload, Span<PgNumeric?> destination)
    {
        return BinaryNullableArray<PgNumeric, NumericCodec>.Read(payload, destination);
    }
}