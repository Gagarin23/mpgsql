using System.Buffers;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Int16 conversion for short and nullable values.</summary>
/// <remarks>
///     Writes only the payload; the field encoder owns its length and SQL NULL marker.
///     Insufficient capacity throws ArgumentException before changing the destination.
///     Reads reject truncated/trailing bytes. NULL writes no bytes and reserves no storage.
/// </remarks>
public static class Int16Converter
{
    public const uint TypeOid = (uint)Mpgsql.TypeOid.Int16;
    public const int ByteCount = 2;
    public static int GetByteCount(short value)
    {
        return Int16Codec.Measure(value);
    }
    public static int GetByteCount(short? value)
    {
        return value.HasValue ? GetByteCount(value.GetValueOrDefault()) : 0;
    }
    public static int Write(short value, Span<byte> destination)
    {
        return BinaryScalar<short, Int16Codec>.Write(value, destination);
    }
    public static int Write(short? value, Span<byte> destination)
    {
        return value.HasValue ? Write(value.GetValueOrDefault(), destination) : 0;
    }
    public static void Write(short value, IBufferWriter<byte> destination)
    {
        BinaryScalar<short, Int16Codec>.Write(value, destination);
    }
    public static void Write(short? value, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (value.HasValue)
        {
            Write(value.GetValueOrDefault(), destination);
        }
    }
    public static short Read(ReadOnlySpan<byte> payload)
    {
        return Int16Codec.Read(payload);
    }
    public static short Read(ReadOnlySequence<byte> payload)
    {
        return Int16Codec.Read(payload);
    }
    public static short? ReadNullable(ReadOnlyMemory<byte>? payload)
    {
        return payload is { } value ? Read(value.Span) : null;
    }
    public static short? ReadNullable(ReadOnlySequence<byte>? payload)
    {
        return payload is { } value ? Read(value) : null;
    }
}