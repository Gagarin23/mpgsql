using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL TimestampTz conversion for PgTimestampTz and nullable values.</summary>
/// <remarks>
/// Writes only the payload; the field encoder owns its length and SQL NULL marker.
/// Insufficient capacity throws ArgumentException before changing the destination.
/// Reads reject truncated/trailing bytes. NULL writes no bytes and reserves no storage.
/// </remarks>
public static partial class TimestampTzConverter
{
    public const uint TypeOid = (uint)Mpgsql.TypeOid.TimestampTz;
    public const int ByteCount = 8;
    public static int GetByteCount(PgTimestampTz value) => TimestampTzCodec.Measure(value);
    public static int GetByteCount(PgTimestampTz? value) => value.HasValue ? GetByteCount(value.GetValueOrDefault()) : 0;
    public static int Write(PgTimestampTz value, Span<byte> destination) => BinaryScalar<PgTimestampTz, TimestampTzCodec>.Write(value, destination);
    public static int Write(PgTimestampTz? value, Span<byte> destination) => value.HasValue ? Write(value.GetValueOrDefault(), destination) : 0;
    public static void Write(PgTimestampTz value, IBufferWriter<byte> destination) => BinaryScalar<PgTimestampTz, TimestampTzCodec>.Write(value, destination);
    public static void Write(PgTimestampTz? value, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (value.HasValue)
        {
            Write(value.GetValueOrDefault(), destination);
        }
    }
    public static PgTimestampTz Read(ReadOnlySpan<byte> payload) => TimestampTzCodec.Read(payload);
    public static PgTimestampTz Read(ReadOnlySequence<byte> payload) => TimestampTzCodec.Read(payload);
    public static PgTimestampTz? ReadNullable(ReadOnlyMemory<byte>? payload) => payload is { } value ? Read(value.Span) : null;
    public static PgTimestampTz? ReadNullable(ReadOnlySequence<byte>? payload) => payload is { } value ? Read(value) : null;
}