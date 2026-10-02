using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Float64 conversion for double and nullable values.</summary>
/// <remarks>
/// Writes only the payload; the field encoder owns its length and SQL NULL marker.
/// Insufficient capacity throws ArgumentException before changing the destination.
/// Reads reject truncated/trailing bytes. NULL writes no bytes and reserves no storage.
/// </remarks>
public static partial class Float64Converter
{
    public const uint TypeOid = (uint)Mpgsql.TypeOid.Float64;
    public const int ByteCount = 8;
    public static int GetByteCount(double value) => Float64Codec.Measure(value);
    public static int GetByteCount(double? value) => value.HasValue ? GetByteCount(value.GetValueOrDefault()) : 0;
    public static int Write(double value, Span<byte> destination) => BinaryScalar<double, Float64Codec>.Write(value, destination);
    public static int Write(double? value, Span<byte> destination) => value.HasValue ? Write(value.GetValueOrDefault(), destination) : 0;
    public static void Write(double value, IBufferWriter<byte> destination) => BinaryScalar<double, Float64Codec>.Write(value, destination);
    public static void Write(double? value, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (value.HasValue)
        {
            Write(value.GetValueOrDefault(), destination);
        }
    }
    public static double Read(ReadOnlySpan<byte> payload) => Float64Codec.Read(payload);
    public static double Read(ReadOnlySequence<byte> payload) => Float64Codec.Read(payload);
    public static double? ReadNullable(ReadOnlyMemory<byte>? payload) => payload is { } value ? Read(value.Span) : null;
    public static double? ReadNullable(ReadOnlySequence<byte>? payload) => payload is { } value ? Read(value) : null;
}