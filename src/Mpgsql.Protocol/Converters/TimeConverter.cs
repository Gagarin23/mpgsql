using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Time conversion for PgTime and nullable values.</summary>
/// <remarks>
///     Writes only the payload; the field encoder owns its length and SQL NULL marker.
///     Insufficient capacity throws ArgumentException before changing the destination.
///     Reads reject truncated/trailing bytes. NULL writes no bytes and reserves no storage.
/// </remarks>
public static partial class TimeConverter
{
    public const uint TypeOid = (uint)Mpgsql.TypeOid.Time;
    public const int ByteCount = 8;
    public static int GetByteCount(PgTime value)
    {
        return TimeCodec.Measure(value);
    }
    public static int GetByteCount(PgTime? value)
    {
        return value.HasValue ? GetByteCount(value.GetValueOrDefault()) : 0;
    }
    public static int Write(PgTime value, Span<byte> destination)
    {
        return BinaryScalar<PgTime, TimeCodec>.Write(value, destination);
    }
    public static int Write(PgTime? value, Span<byte> destination)
    {
        return value.HasValue ? Write(value.GetValueOrDefault(), destination) : 0;
    }
    public static void Write(PgTime value, IBufferWriter<byte> destination)
    {
        BinaryScalar<PgTime, TimeCodec>.Write(value, destination);
    }
    public static void Write(PgTime? value, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (value.HasValue)
        {
            Write(value.GetValueOrDefault(), destination);
        }
    }
    public static PgTime Read(ReadOnlySpan<byte> payload)
    {
        return TimeCodec.Read(payload);
    }
    public static PgTime Read(ReadOnlySequence<byte> payload)
    {
        return TimeCodec.Read(payload);
    }
    public static PgTime? ReadNullable(ReadOnlyMemory<byte>? payload)
    {
        return payload is { } value ? Read(value.Span) : null;
    }
    public static PgTime? ReadNullable(ReadOnlySequence<byte>? payload)
    {
        return payload is { } value ? Read(value) : null;
    }
}