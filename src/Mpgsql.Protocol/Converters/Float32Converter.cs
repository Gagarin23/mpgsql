using System.Buffers;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Float32 conversion for float and nullable values.</summary>
/// <remarks>
///     Writes only the payload; the field encoder owns its length and SQL NULL marker.
///     Insufficient capacity throws ArgumentException before changing the destination.
///     Reads reject truncated/trailing bytes. NULL writes no bytes and reserves no storage.
/// </remarks>
public static class Float32Converter
{
    public const uint TypeOid = (uint)Mpgsql.TypeOid.Float32;
    public const int ByteCount = 4;
    public static int GetByteCount(float value)
    {
        return Float32Codec.Measure(value);
    }
    public static int GetByteCount(float? value)
    {
        return value.HasValue ? GetByteCount(value.GetValueOrDefault()) : 0;
    }
    public static int Write(float value, Span<byte> destination)
    {
        return BinaryScalar<float, Float32Codec>.Write(value, destination);
    }
    public static int Write(float? value, Span<byte> destination)
    {
        return value.HasValue ? Write(value.GetValueOrDefault(), destination) : 0;
    }
    public static void Write(float value, IBufferWriter<byte> destination)
    {
        BinaryScalar<float, Float32Codec>.Write(value, destination);
    }
    public static void Write(float? value, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (value.HasValue)
        {
            Write(value.GetValueOrDefault(), destination);
        }
    }
    public static float Read(ReadOnlySpan<byte> payload)
    {
        return Float32Codec.Read(payload);
    }
    public static float Read(ReadOnlySequence<byte> payload)
    {
        return Float32Codec.Read(payload);
    }
    public static float? ReadNullable(ReadOnlyMemory<byte>? payload)
    {
        return payload is { } value ? Read(value.Span) : null;
    }
    public static float? ReadNullable(ReadOnlySequence<byte>? payload)
    {
        return payload is { } value ? Read(value) : null;
    }
}