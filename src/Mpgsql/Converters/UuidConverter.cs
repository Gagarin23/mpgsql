using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Uuid conversion for Guid and nullable values.</summary>
/// <remarks>
/// Writes only the payload; the field encoder owns its length and SQL NULL marker.
/// Insufficient capacity throws ArgumentException before changing the destination.
/// Reads reject truncated/trailing bytes. NULL writes no bytes and reserves no storage.
/// </remarks>
public static partial class UuidConverter
{
    public const uint TypeOid = (uint)Mpgsql.TypeOid.Uuid;
    public const int ByteCount = 16;
    public static int GetByteCount(Guid value) => UuidCodec.Measure(value);
    public static int GetByteCount(Guid? value) => value.HasValue ? GetByteCount(value.GetValueOrDefault()) : 0;
    public static int Write(Guid value, Span<byte> destination) => BinaryScalar<Guid, UuidCodec>.Write(value, destination);
    public static int Write(Guid? value, Span<byte> destination) => value.HasValue ? Write(value.GetValueOrDefault(), destination) : 0;
    public static void Write(Guid value, IBufferWriter<byte> destination) => BinaryScalar<Guid, UuidCodec>.Write(value, destination);
    public static void Write(Guid? value, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (value.HasValue)
        {
            Write(value.GetValueOrDefault(), destination);
        }
    }
    public static Guid Read(ReadOnlySpan<byte> payload) => UuidCodec.Read(payload);
    public static Guid Read(ReadOnlySequence<byte> payload) => UuidCodec.Read(payload);
    public static Guid? ReadNullable(ReadOnlyMemory<byte>? payload) => payload is { } value ? Read(value.Span) : null;
    public static Guid? ReadNullable(ReadOnlySequence<byte>? payload) => payload is { } value ? Read(value) : null;
}