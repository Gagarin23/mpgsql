using System.Buffers;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Boolean conversion for bool and nullable values.</summary>
/// <remarks>
///     Writes only the payload; the field encoder owns its length and SQL NULL marker.
///     Insufficient capacity throws ArgumentException before changing the destination.
///     Reads reject truncated/trailing bytes. NULL writes no bytes and reserves no storage.
/// </remarks>
public static class BooleanConverter
{
    public const uint TypeOid = (uint)Mpgsql.TypeOid.Boolean;
    public const int ByteCount = 1;
    public static int GetByteCount(bool value)
    {
        return BooleanCodec.Measure(value);
    }
    public static int GetByteCount(bool? value)
    {
        return value.HasValue ? GetByteCount(value.GetValueOrDefault()) : 0;
    }
    public static int Write(bool value, Span<byte> destination)
    {
        return BinaryScalar<bool, BooleanCodec>.Write(value, destination);
    }
    public static int Write(bool? value, Span<byte> destination)
    {
        return value.HasValue ? Write(value.GetValueOrDefault(), destination) : 0;
    }
    public static void Write(bool value, IBufferWriter<byte> destination)
    {
        BinaryScalar<bool, BooleanCodec>.Write(value, destination);
    }
    public static void Write(bool? value, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (value.HasValue)
        {
            Write(value.GetValueOrDefault(), destination);
        }
    }
    public static bool Read(ReadOnlySpan<byte> payload)
    {
        return BooleanCodec.Read(payload);
    }
    public static bool Read(ReadOnlySequence<byte> payload)
    {
        return BooleanCodec.Read(payload);
    }
    public static bool? ReadNullable(ReadOnlyMemory<byte>? payload)
    {
        return payload is { } value ? Read(value.Span) : null;
    }
    public static bool? ReadNullable(ReadOnlySequence<byte>? payload)
    {
        return payload is { } value ? Read(value) : null;
    }
}