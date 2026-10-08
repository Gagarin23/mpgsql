using System.Buffers;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Oid conversion for uint and nullable values.</summary>
/// <remarks>
///     Writes only the payload; the field encoder owns its length and SQL NULL marker.
///     Insufficient capacity throws ArgumentException before changing the destination.
///     Reads reject truncated/trailing bytes. NULL writes no bytes and reserves no storage.
/// </remarks>
public static class OidConverter
{
    public const uint TypeOid = (uint)Mpgsql.TypeOid.Oid;
    public const int ByteCount = 4;
    public static int GetByteCount(uint value)
    {
        return OidCodec.Measure(value);
    }
    public static int GetByteCount(uint? value)
    {
        return value.HasValue ? GetByteCount(value.GetValueOrDefault()) : 0;
    }
    public static int Write(uint value, Span<byte> destination)
    {
        return BinaryScalar<uint, OidCodec>.Write(value, destination);
    }
    public static int Write(uint? value, Span<byte> destination)
    {
        return value.HasValue ? Write(value.GetValueOrDefault(), destination) : 0;
    }
    public static void Write(uint value, IBufferWriter<byte> destination)
    {
        BinaryScalar<uint, OidCodec>.Write(value, destination);
    }
    public static void Write(uint? value, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (value.HasValue)
        {
            Write(value.GetValueOrDefault(), destination);
        }
    }
    public static uint Read(ReadOnlySpan<byte> payload)
    {
        return OidCodec.Read(payload);
    }
    public static uint Read(ReadOnlySequence<byte> payload)
    {
        return OidCodec.Read(payload);
    }
    public static uint? ReadNullable(ReadOnlyMemory<byte>? payload)
    {
        return payload is { } value ? Read(value.Span) : null;
    }
    public static uint? ReadNullable(ReadOnlySequence<byte>? payload)
    {
        return payload is { } value ? Read(value) : null;
    }
}