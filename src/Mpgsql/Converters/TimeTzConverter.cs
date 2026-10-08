using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL TimeTz conversion for PgTimeTz and nullable values.</summary>
/// <remarks>
///     Writes only the payload; the field encoder owns its length and SQL NULL marker.
///     Insufficient capacity throws ArgumentException before changing the destination.
///     Reads reject truncated/trailing bytes. NULL writes no bytes and reserves no storage.
/// </remarks>
public static class TimeTzConverter
{
    public const uint TypeOid = (uint)Mpgsql.TypeOid.TimeTz;
    public const int ByteCount = 12;
    public static int GetByteCount(PgTimeTz value)
    {
        return TimeTzCodec.Measure(value);
    }
    public static int GetByteCount(PgTimeTz? value)
    {
        return value.HasValue ? GetByteCount(value.GetValueOrDefault()) : 0;
    }
    public static int Write(PgTimeTz value, Span<byte> destination)
    {
        return BinaryScalar<PgTimeTz, TimeTzCodec>.Write(value, destination);
    }
    public static int Write(PgTimeTz? value, Span<byte> destination)
    {
        return value.HasValue ? Write(value.GetValueOrDefault(), destination) : 0;
    }
    public static void Write(PgTimeTz value, IBufferWriter<byte> destination)
    {
        BinaryScalar<PgTimeTz, TimeTzCodec>.Write(value, destination);
    }
    public static void Write(PgTimeTz? value, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (value.HasValue)
        {
            Write(value.GetValueOrDefault(), destination);
        }
    }
    public static PgTimeTz Read(ReadOnlySpan<byte> payload)
    {
        return TimeTzCodec.Read(payload);
    }
    public static PgTimeTz Read(ReadOnlySequence<byte> payload)
    {
        return TimeTzCodec.Read(payload);
    }
    public static PgTimeTz? ReadNullable(ReadOnlyMemory<byte>? payload)
    {
        return payload is { } value ? Read(value.Span) : null;
    }
    public static PgTimeTz? ReadNullable(ReadOnlySequence<byte>? payload)
    {
        return payload is { } value ? Read(value) : null;
    }
}