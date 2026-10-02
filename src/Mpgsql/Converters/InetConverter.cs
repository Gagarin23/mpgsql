using System.Buffers;
using System.Net;
using Mpgsql.Types;

namespace Mpgsql.Converters;

/// <summary>Binary PostgreSQL Inet conversion without IPAddress allocations.</summary>
public static class InetConverter
{
    public const uint TypeOid = (uint)Mpgsql.TypeOid.Inet;
    public static int GetByteCount(PgInet value) => InetCodec.Measure(value);
    public static int GetByteCount(PgInet? value) => value.HasValue ? GetByteCount(value.Value) : 0;
    public static int Write(PgInet value, Span<byte> destination) => BinaryScalar<PgInet, InetCodec>.Write(value, destination);
    public static int Write(PgInet? value, Span<byte> destination) => value.HasValue ? Write(value.Value, destination) : 0;
    public static void Write(PgInet value, IBufferWriter<byte> destination) => BinaryScalar<PgInet, InetCodec>.Write(value, destination);
    public static void Write(PgInet? value, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (value.HasValue)
        {
            Write(value.Value, destination);
        }
    }
    public static int Write(IPAddress address, Span<byte> destination,
        int? prefixLength = null) => Write(PgInet.FromIPAddress(address, prefixLength), destination);
    public static PgInet Read(ReadOnlySpan<byte> payload) => InetCodec.Read(payload);
    public static PgInet Read(ReadOnlySequence<byte> payload) => InetCodec.Read(payload);
    public static PgInet? ReadNullable(ReadOnlyMemory<byte>? payload) => payload is { } value ? Read(value.Span) : null;
    public static PgInet? ReadNullable(ReadOnlySequence<byte>? payload) => payload is { } value ? Read(value) : null;
}