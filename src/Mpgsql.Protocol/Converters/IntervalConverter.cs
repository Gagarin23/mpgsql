using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Interval conversion for PgInterval and nullable values.</summary>
/// <remarks>
///     Writes only the payload; the field encoder owns its length and SQL NULL marker.
///     Insufficient capacity throws ArgumentException before changing the destination.
///     Reads reject truncated/trailing bytes. NULL writes no bytes and reserves no storage.
/// </remarks>
public static partial class IntervalConverter
{
    public const uint TypeOid = (uint)Mpgsql.TypeOid.Interval;
    public const int ByteCount = 16;
    public static int GetByteCount(PgInterval value)
    {
        return IntervalCodec.Measure(value);
    }
    public static int GetByteCount(PgInterval? value)
    {
        return value.HasValue ? GetByteCount(value.GetValueOrDefault()) : 0;
    }
    public static int Write(PgInterval value, Span<byte> destination)
    {
        return BinaryScalar<PgInterval, IntervalCodec>.Write(value, destination);
    }
    public static int Write(PgInterval? value, Span<byte> destination)
    {
        return value.HasValue ? Write(value.GetValueOrDefault(), destination) : 0;
    }
    public static void Write(PgInterval value, IBufferWriter<byte> destination)
    {
        BinaryScalar<PgInterval, IntervalCodec>.Write(value, destination);
    }
    public static void Write(PgInterval? value, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (value.HasValue)
        {
            Write(value.GetValueOrDefault(), destination);
        }
    }
    public static PgInterval Read(ReadOnlySpan<byte> payload)
    {
        return IntervalCodec.Read(payload);
    }
    public static PgInterval Read(ReadOnlySequence<byte> payload)
    {
        return IntervalCodec.Read(payload);
    }
    public static PgInterval? ReadNullable(ReadOnlyMemory<byte>? payload)
    {
        return payload is { } value ? Read(value.Span) : null;
    }
    public static PgInterval? ReadNullable(ReadOnlySequence<byte>? payload)
    {
        return payload is { } value ? Read(value) : null;
    }
}