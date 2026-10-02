using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Date conversion for PgDate and nullable values.</summary>
/// <remarks>
/// Writes only the payload; the field encoder owns its length and SQL NULL marker.
/// Insufficient capacity throws ArgumentException before changing the destination.
/// Reads reject truncated/trailing bytes. NULL writes no bytes and reserves no storage.
/// </remarks>
public static partial class DateConverter
{
    public const uint TypeOid = (uint)Mpgsql.TypeOid.Date;
    public const int ByteCount = 4;
    public static int GetByteCount(PgDate value) => DateCodec.Measure(value);
    public static int GetByteCount(PgDate? value) => value.HasValue ? GetByteCount(value.GetValueOrDefault()) : 0;
    public static int Write(PgDate value, Span<byte> destination) => BinaryScalar<PgDate, DateCodec>.Write(value, destination);
    public static int Write(PgDate? value, Span<byte> destination) => value.HasValue ? Write(value.GetValueOrDefault(), destination) : 0;
    public static void Write(PgDate value, IBufferWriter<byte> destination) => BinaryScalar<PgDate, DateCodec>.Write(value, destination);
    public static void Write(PgDate? value, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (value.HasValue)
        {
            Write(value.GetValueOrDefault(), destination);
        }
    }
    public static PgDate Read(ReadOnlySpan<byte> payload) => DateCodec.Read(payload);
    public static PgDate Read(ReadOnlySequence<byte> payload) => DateCodec.Read(payload);
    public static PgDate? ReadNullable(ReadOnlyMemory<byte>? payload) => payload is { } value ? Read(value.Span) : null;
    public static PgDate? ReadNullable(ReadOnlySequence<byte>? payload) => payload is { } value ? Read(value) : null;
}