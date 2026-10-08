using System.Buffers;

namespace Mpgsql.Converters;

/// <summary>PostgreSQL bpchar binary payload in UTF-8.</summary>
/// <remarks>
///     NULL writes no bytes. Received trailing spaces are retained. Capacity and UTF-16 validity are checked before
///     writing; PostgreSQL applies padding and length constraints.
/// </remarks>
public static class BpCharConverter
{
    public const uint TypeOid = (uint)Mpgsql.TypeOid.BpChar;
    public static int GetByteCount(string? value)
    {
        return value is null ? 0 : BpCharCodec.Measure(value);
    }
    public static int Write(string? value, Span<byte> destination)
    {
        return value is null ? 0 : BinaryScalar<string, BpCharCodec>.Write(value, destination);
    }
    public static void Write(string? value, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (value is not null)
        {
            BinaryScalar<string, BpCharCodec>.Write(value, destination);
        }
    }
    public static string Read(ReadOnlySpan<byte> payload)
    {
        return BpCharCodec.Read(payload);
    }
    public static string Read(ReadOnlySequence<byte> payload)
    {
        return BpCharCodec.Read(payload);
    }
    public static string? ReadNullable(ReadOnlyMemory<byte>? payload)
    {
        return payload is { } value ? Read(value.Span) : null;
    }
    public static string? ReadNullable(ReadOnlySequence<byte>? payload)
    {
        return payload is { } value ? Read(value) : null;
    }
    /// <summary>Returns borrowed bytes without content validation; consume before releasing the input buffer.</summary>
    public static ReadOnlySpan<byte> ReadUtf8(ReadOnlySpan<byte> payload)
    {
        return Utf8Payload.ReadUtf8(payload, false);
    }
    public static ReadOnlySequence<byte> ReadUtf8(ReadOnlySequence<byte> payload)
    {
        return Utf8Payload.ReadUtf8(payload, false);
    }
    public static int WriteUtf8(ReadOnlySpan<byte> value, Span<byte> destination)
    {
        return Utf8Payload.WriteUtf8(value, destination, false);
    }
}