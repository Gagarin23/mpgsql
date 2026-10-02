using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Int32 conversion for int and nullable values.</summary>
/// <remarks>
/// Writes only the payload; the field encoder owns its length and SQL NULL marker.
/// Insufficient capacity throws ArgumentException before changing the destination.
/// Reads reject truncated/trailing bytes. NULL writes no bytes and reserves no storage.
/// </remarks>
public static partial class Int32Converter
{
    public const uint TypeOid = (uint)Mpgsql.TypeOid.Int32;
    public const int ByteCount = 4;
    public static int GetByteCount(int value) => Int32Codec.Measure(value);
    public static int GetByteCount(int? value) => value.HasValue ? GetByteCount(value.GetValueOrDefault()) : 0;
    public static int Write(int value, Span<byte> destination) => BinaryScalar<int, Int32Codec>.Write(value, destination);
    public static int Write(int? value, Span<byte> destination) => value.HasValue ? Write(value.GetValueOrDefault(), destination) : 0;
    public static void Write(int value, IBufferWriter<byte> destination) => BinaryScalar<int, Int32Codec>.Write(value, destination);
    public static void Write(int? value, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (value.HasValue)
        {
            Write(value.GetValueOrDefault(), destination);
        }
    }
    public static int Read(ReadOnlySpan<byte> payload) => Int32Codec.Read(payload);
    public static int Read(ReadOnlySequence<byte> payload) => Int32Codec.Read(payload);
    public static int? ReadNullable(ReadOnlyMemory<byte>? payload) => payload is { } value ? Read(value.Span) : null;
    public static int? ReadNullable(ReadOnlySequence<byte>? payload) => payload is { } value ? Read(value) : null;
}