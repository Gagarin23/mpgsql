using System.Buffers;
using System.Net;
using Mpgsql.Types;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Cidr conversion without IPAddress allocations.</summary>
public static class CidrConverter
{
    public const uint TypeOid = (uint)Mpgsql.TypeOid.Cidr;
    public static int GetByteCount(PgInet value)
    {
        return CidrCodec.Measure(value);
    }
    public static int GetByteCount(PgInet? value)
    {
        return value.HasValue ? GetByteCount(value.Value) : 0;
    }
    public static int Write(PgInet value, Span<byte> destination)
    {
        return BinaryScalar<PgInet, CidrCodec>.Write(value, destination);
    }
    public static int Write(PgInet? value, Span<byte> destination)
    {
        return value.HasValue ? Write(value.Value, destination) : 0;
    }
    public static void Write(PgInet value, IBufferWriter<byte> destination)
    {
        BinaryScalar<PgInet, CidrCodec>.Write(value, destination);
    }
    public static void Write(PgInet? value, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (value.HasValue)
        {
            Write(value.Value, destination);
        }
    }
    public static int Write(IPAddress address, Span<byte> destination,
        int? prefixLength = null)
    {
        return Write(PgInet.FromIPAddress(address, prefixLength), destination);
    }
    public static PgInet Read(ReadOnlySpan<byte> payload)
    {
        return CidrCodec.Read(payload);
    }
    public static PgInet Read(ReadOnlySequence<byte> payload)
    {
        return CidrCodec.Read(payload);
    }
    public static PgInet? ReadNullable(ReadOnlyMemory<byte>? payload)
    {
        return payload is { } value ? Read(value.Span) : null;
    }
    public static PgInet? ReadNullable(ReadOnlySequence<byte>? payload)
    {
        return payload is { } value ? Read(value) : null;
    }
}