using System.Buffers;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Money conversion for long and nullable values.</summary>
/// <remarks>
///     Writes only the payload; the field encoder owns its length and SQL NULL marker.
///     Insufficient capacity throws ArgumentException before changing the destination.
///     Reads reject truncated/trailing bytes. NULL writes no bytes and reserves no storage.
/// </remarks>
public static class MoneyConverter
{
    public const uint TypeOid = (uint)Mpgsql.TypeOid.Money;
    public const int ByteCount = 8;
    public static int GetByteCount(long value)
    {
        return MoneyCodec.Measure(value);
    }
    public static int GetByteCount(long? value)
    {
        return value.HasValue ? GetByteCount(value.GetValueOrDefault()) : 0;
    }
    public static int Write(long value, Span<byte> destination)
    {
        return BinaryScalar<long, MoneyCodec>.Write(value, destination);
    }
    public static int Write(long? value, Span<byte> destination)
    {
        return value.HasValue ? Write(value.GetValueOrDefault(), destination) : 0;
    }
    public static void Write(long value, IBufferWriter<byte> destination)
    {
        BinaryScalar<long, MoneyCodec>.Write(value, destination);
    }
    public static void Write(long? value, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (value.HasValue)
        {
            Write(value.GetValueOrDefault(), destination);
        }
    }
    public static long Read(ReadOnlySpan<byte> payload)
    {
        return MoneyCodec.Read(payload);
    }
    public static long Read(ReadOnlySequence<byte> payload)
    {
        return MoneyCodec.Read(payload);
    }
    public static long? ReadNullable(ReadOnlyMemory<byte>? payload)
    {
        return payload is { } value ? Read(value.Span) : null;
    }
    public static long? ReadNullable(ReadOnlySequence<byte>? payload)
    {
        return payload is { } value ? Read(value) : null;
    }
}