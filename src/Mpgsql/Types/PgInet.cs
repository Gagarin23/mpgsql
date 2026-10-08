using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Mpgsql.Types;

/// <summary>An IPv4/IPv6 address and prefix, stored without allocating an IPAddress.</summary>
/// <remarks>IPv4 occupies the low 32 bits of Address. IPv6 uses all 128 bits in network order.</remarks>
public readonly record struct PgInet(UInt128 Address, byte PrefixLength, bool IsIPv6 = false)
{
    public static PgInet FromIPAddress(IPAddress address, int? prefixLength = null)
    {
        ArgumentNullException.ThrowIfNull(address);
        var ipv6 = address.AddressFamily == AddressFamily.InterNetworkV6;
        if (!ipv6 && address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException("Only IPv4 and IPv6 addresses are supported.", nameof(address));
        }
        if (ipv6 && address.ScopeId != 0)
        {
            throw new ArgumentException("PostgreSQL inet cannot retain an IPv6 scope ID.", nameof(address));
        }
        var bits = prefixLength ?? (ipv6 ? 128 : 32);
        if ((uint)bits > (ipv6 ? 128u : 32u))
        {
            throw new ArgumentOutOfRangeException(nameof(prefixLength));
        }
        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        return new PgInet(ipv6 ? BinaryPrimitives.ReadUInt128BigEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes), (byte)bits, ipv6);
    }

    public IPAddress ToIPAddress()
    {
        Span<byte> bytes = stackalloc byte[16];
        if (IsIPv6)
        {
            BinaryPrimitives.WriteUInt128BigEndian(bytes, Address);
        }
        else
        {
            if (Address > uint.MaxValue)
            {
                throw new OverflowException("Invalid IPv4 address.");
            }
            BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)Address);
        }
        return new IPAddress(bytes[..(IsIPv6 ? 16 : 4)]);
    }
}