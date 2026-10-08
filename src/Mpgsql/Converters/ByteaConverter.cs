using System.Buffers;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL bytea; bytes are copied verbatim, without a terminator.</summary>
/// <remarks>Writes only payload. NULL is distinct from an empty bytea. Read returns owned memory.</remarks>
public static class ByteaConverter
{
    public const uint TypeOid = (uint)Mpgsql.TypeOid.Bytea;
    public static int GetByteCount(ReadOnlyMemory<byte> value)
    {
        return value.Length;
    }
    public static int GetByteCount(ReadOnlyMemory<byte>? value)
    {
        return value?.Length ?? 0;
    }
    public static int Write(ReadOnlyMemory<byte> value, Span<byte> destination)
    {
        return BinaryScalar<ReadOnlyMemory<byte>, ByteaCodec>.Write(value, destination);
    }
    public static int Write(ReadOnlyMemory<byte>? value, Span<byte> destination)
    {
        return value.HasValue ? Write(value.Value, destination) : 0;
    }
    public static void Write(ReadOnlyMemory<byte> value, IBufferWriter<byte> destination)
    {
        BinaryScalar<ReadOnlyMemory<byte>, ByteaCodec>.Write(value, destination);
    }
    public static void Write(ReadOnlyMemory<byte>? value, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (value.HasValue)
        {
            Write(value.Value, destination);
        }
    }
    public static ReadOnlyMemory<byte> Read(ReadOnlySpan<byte> payload)
    {
        return ByteaCodec.Read(payload);
    }
    public static ReadOnlyMemory<byte> Read(ReadOnlySequence<byte> payload)
    {
        return ByteaCodec.Read(payload);
    }
    public static ReadOnlyMemory<byte>? ReadNullable(ReadOnlyMemory<byte>? payload)
    {
        return payload is { } value ? Read(value.Span) : (ReadOnlyMemory<byte>?)null;
    }
    public static ReadOnlyMemory<byte>? ReadNullable(ReadOnlySequence<byte>? payload)
    {
        return payload is { } value ? Read(value) : (ReadOnlyMemory<byte>?)null;
    }
    /// <summary>Returns borrowed bytes; consume them before releasing the network buffer.</summary>
    public static ReadOnlyMemory<byte> ReadBorrowed(ReadOnlyMemory<byte> payload)
    {
        return payload;
    }
    /// <summary>Returns a borrowed sequence without allocating or copying.</summary>
    public static ReadOnlySequence<byte> ReadBorrowed(ReadOnlySequence<byte> payload)
    {
        return payload;
    }
    public static int Read(ReadOnlySpan<byte> payload, Span<byte> destination)
    {
        BinaryPayload.RequireCapacity(payload.Length, destination.Length);
        payload.CopyTo(destination);
        return payload.Length;
    }
    public static int Read(ReadOnlySequence<byte> payload, Span<byte> destination)
    {
        var count = checked((int)payload.Length);
        BinaryPayload.RequireCapacity(count, destination.Length);
        if (payload.IsSingleSegment)
        {
            return Read(payload.FirstSpan, destination);
        }
        BinaryPayload.RequireSeparate(payload, destination[..count]);
        payload.CopyTo(destination);
        return count;
    }
}