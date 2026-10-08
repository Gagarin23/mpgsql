using System.Buffers;

namespace Mpgsql.Converters;

/// <summary>PostgreSQL name binary payload in UTF-8.</summary>
/// <remarks>
///     NULL writes no bytes. Capacity and UTF-16 validity are checked before writing. PostgreSQL validates its
///     build's name length limit; the client does not truncate values.
/// </remarks>
public static class NameConverter
{
    public const uint TypeOid = (uint)Mpgsql.TypeOid.Name;
    public static int GetByteCount(string? value)
    {
        return value is null ? 0 : NameCodec.Measure(value);
    }
    public static int Write(string? value, Span<byte> destination)
    {
        return value is null ? 0 : BinaryScalar<string, NameCodec>.Write(value, destination);
    }
    public static void Write(string? value, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (value is not null)
        {
            BinaryScalar<string, NameCodec>.Write(value, destination);
        }
    }
    public static string Read(ReadOnlySpan<byte> payload)
    {
        return NameCodec.Read(payload);
    }
    public static string Read(ReadOnlySequence<byte> payload)
    {
        return NameCodec.Read(payload);
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