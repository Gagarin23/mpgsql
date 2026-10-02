using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Money conversion for long and nullable values.</summary>
/// <remarks>
/// Writes only the payload; the field encoder owns its length and SQL NULL marker.
/// Insufficient capacity throws ArgumentException before changing the destination.
/// Reads reject truncated/trailing bytes. NULL writes no bytes and reserves no storage.
/// </remarks>
public static partial class MoneyConverter
{
    public const uint TypeOid = (uint)Mpgsql.TypeOid.Money;
    public const int ByteCount = 8;
    public static int GetByteCount(long value) => MoneyCodec.Measure(value);
    public static int GetByteCount(long? value) => value.HasValue ? GetByteCount(value.GetValueOrDefault()) : 0;
    public static int Write(long value, Span<byte> destination) => BinaryScalar<long, MoneyCodec>.Write(value, destination);
    public static int Write(long? value, Span<byte> destination) => value.HasValue ? Write(value.GetValueOrDefault(), destination) : 0;
    public static void Write(long value, IBufferWriter<byte> destination) => BinaryScalar<long, MoneyCodec>.Write(value, destination);
    public static void Write(long? value, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (value.HasValue)
        {
            Write(value.GetValueOrDefault(), destination);
        }
    }
    public static long Read(ReadOnlySpan<byte> payload) => MoneyCodec.Read(payload);
    public static long Read(ReadOnlySequence<byte> payload) => MoneyCodec.Read(payload);
    public static long? ReadNullable(ReadOnlyMemory<byte>? payload) => payload is { } value ? Read(value.Span) : null;
    public static long? ReadNullable(ReadOnlySequence<byte>? payload) => payload is { } value ? Read(value) : null;
}