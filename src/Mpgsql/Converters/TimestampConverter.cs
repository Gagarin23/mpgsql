using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Timestamp conversion for PgTimestamp and nullable values.</summary>
/// <remarks>
///     Writes only the payload; the field encoder owns its length and SQL NULL marker.
///     Insufficient capacity throws ArgumentException before changing the destination.
///     Reads reject truncated/trailing bytes. NULL writes no bytes and reserves no storage.
/// </remarks>
public static partial class TimestampConverter
{
    public const uint TypeOid = (uint)Mpgsql.TypeOid.Timestamp;
    public const int ByteCount = 8;
    public static int GetByteCount(PgTimestamp value)
    {
        return TimestampCodec.Measure(value);
    }
    public static int GetByteCount(PgTimestamp? value)
    {
        return value.HasValue ? GetByteCount(value.GetValueOrDefault()) : 0;
    }
    public static int Write(PgTimestamp value, Span<byte> destination)
    {
        return BinaryScalar<PgTimestamp, TimestampCodec>.Write(value, destination);
    }
    public static int Write(PgTimestamp? value, Span<byte> destination)
    {
        return value.HasValue ? Write(value.GetValueOrDefault(), destination) : 0;
    }
    public static void Write(PgTimestamp value, IBufferWriter<byte> destination)
    {
        BinaryScalar<PgTimestamp, TimestampCodec>.Write(value, destination);
    }
    public static void Write(PgTimestamp? value, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (value.HasValue)
        {
            Write(value.GetValueOrDefault(), destination);
        }
    }
    public static PgTimestamp Read(ReadOnlySpan<byte> payload)
    {
        return TimestampCodec.Read(payload);
    }
    public static PgTimestamp Read(ReadOnlySequence<byte> payload)
    {
        return TimestampCodec.Read(payload);
    }
    public static PgTimestamp? ReadNullable(ReadOnlyMemory<byte>? payload)
    {
        return payload is { } value ? Read(value.Span) : null;
    }
    public static PgTimestamp? ReadNullable(ReadOnlySequence<byte>? payload)
    {
        return payload is { } value ? Read(value) : null;
    }
}