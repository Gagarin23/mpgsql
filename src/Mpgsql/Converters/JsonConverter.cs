using System.Buffers;

namespace Mpgsql.Converters;

/// <summary>PostgreSQL Json binary payload in UTF-8; jsonb includes version byte 1.</summary>
/// <remarks>NULL writes no bytes. Capacity and UTF-16 validity are checked before writing. Syntax is checked by PostgreSQL.</remarks>
public static class JsonConverter
{
    public const uint TypeOid = (uint)Mpgsql.TypeOid.Json;
    public static int GetByteCount(string? value) => value is null ? 0 : JsonCodec.Measure(value);
    public static int Write(string? value, Span<byte> destination) => value is null ? 0 : BinaryScalar<string, JsonCodec>.Write(value, destination);
    public static void Write(string? value, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (value is not null)
        {
            BinaryScalar<string, JsonCodec>.Write(value, destination);
        }
    }
    public static string Read(ReadOnlySpan<byte> payload) => JsonCodec.Read(payload);
    public static string Read(ReadOnlySequence<byte> payload) => JsonCodec.Read(payload);
    public static string? ReadNullable(ReadOnlyMemory<byte>? payload) => payload is { } value ? Read(value.Span) : null;
    public static string? ReadNullable(ReadOnlySequence<byte>? payload) => payload is { } value ? Read(value) : null;
    /// <summary>Returns validated borrowed UTF-8 bytes; consume before releasing the input buffer.</summary>
    public static ReadOnlySpan<byte> ReadUtf8(ReadOnlySpan<byte> payload) => Utf8Payload.ReadUtf8(payload, false);
    public static ReadOnlySequence<byte> ReadUtf8(ReadOnlySequence<byte> payload) => Utf8Payload.ReadUtf8(payload, false);
    public static int WriteUtf8(ReadOnlySpan<byte> value, Span<byte> destination) => Utf8Payload.WriteUtf8(value, destination, false);
}