using System.Buffers;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL jsonb for UTF-8 JSON bytes in Memory&lt;byte&gt;.</summary>
/// <remarks>
/// Values exclude the wire version byte 1, which is added on write and removed on read.
/// NULL writes no bytes. Capacity and overlap are checked before writing.
/// Read returns owned memory. Value contents are checked by PostgreSQL without client-side scans.
/// </remarks>
public static class JsonbConverter
{
    public const uint TypeOid = (uint)Mpgsql.TypeOid.Jsonb;
    public static int GetByteCount(Memory<byte> value) => JsonbCodec.Measure(value);
    public static int GetByteCount(Memory<byte>? value) => value.HasValue ? GetByteCount(value.Value) : 0;
    public static int Write(Memory<byte> value, Span<byte> destination) => BinaryScalar<Memory<byte>, JsonbCodec>.Write(value, destination);
    public static int Write(Memory<byte>? value, Span<byte> destination) => value.HasValue ? Write(value.Value, destination) : 0;
    public static void Write(Memory<byte> value, IBufferWriter<byte> destination) => BinaryScalar<Memory<byte>, JsonbCodec>.Write(value, destination);
    public static void Write(Memory<byte>? value, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (value.HasValue)
        {
            Write(value.Value, destination);
        }
    }
    public static Memory<byte> Read(ReadOnlySpan<byte> payload) => JsonbCodec.Read(payload);
    public static Memory<byte> Read(ReadOnlySequence<byte> payload) => JsonbCodec.Read(payload);
    public static Memory<byte>? ReadNullable(ReadOnlyMemory<byte>? payload) => payload is { } value ? Read(value.Span) : (Memory<byte>?)null;
    public static Memory<byte>? ReadNullable(ReadOnlySequence<byte>? payload) => payload is { } value ? Read(value) : (Memory<byte>?)null;
    /// <summary>Returns borrowed bytes without content validation; consume before releasing the input buffer.</summary>
    public static ReadOnlySpan<byte> ReadUtf8(ReadOnlySpan<byte> payload) => Utf8Payload.ReadUtf8(payload, true);
    public static ReadOnlySequence<byte> ReadUtf8(ReadOnlySequence<byte> payload) => Utf8Payload.ReadUtf8(payload, true);
    public static int WriteUtf8(ReadOnlySpan<byte> value, Span<byte> destination) => Utf8Payload.WriteUtf8(value, destination, true);
}
