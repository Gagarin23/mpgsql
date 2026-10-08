using System.Buffers.Binary;
using Mpgsql.Types;

namespace Mpgsql.Converters;

internal static class NetworkPayload
{
    internal static bool IsValid(PgInet value, bool cidr)
    {
        var maxBits = value.IsIPv6 ? 128 : 32;
        if (value.PrefixLength > maxBits || !value.IsIPv6 && value.Address > uint.MaxValue)
        {
            return false;
        }
        if (!cidr || value.PrefixLength == maxBits)
        {
            return true;
        }
        if (value.PrefixLength == 0)
        {
            return value.Address == 0;
        }
        var hostMask = ((UInt128)1 << maxBits - value.PrefixLength) - 1;
        return (value.Address & hostMask) == 0;
    }

    internal static int Measure(PgInet value, bool cidr)
    {
        if (!IsValid(value, cidr))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Invalid address, prefix, or CIDR host bits.");
        }
        return value.IsIPv6 ? 20 : 8;
    }

    internal static int Write(PgInet value, Span<byte> destination,
        bool cidr)
    {
        // PostgreSQL's family numbers (2,3), prefix bits, is_cidr, address byte length, network-order address.
        destination[0] = value.IsIPv6 ? (byte)3 : (byte)2;
        destination[1] = value.PrefixLength;
        destination[2] = cidr ? (byte)1 : (byte)0;
        destination[3] = value.IsIPv6 ? (byte)16 : (byte)4;
        if (value.IsIPv6)
        {
            BinaryPrimitives.WriteUInt128BigEndian(destination[4..], value.Address);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(destination[4..], (uint)value.Address);
        }
        return value.IsIPv6 ? 20 : 8;
    }

    internal static PgInet Read(ReadOnlySpan<byte> payload, bool cidr)
    {
        if (payload.Length < 4 || payload[0] is not (2 or 3))
        {
            throw new InvalidDataException("Invalid PostgreSQL address family.");
        }
        var ipv6 = payload[0] == 3;
        var addressSize = ipv6 ? 16 : 4;
        if (payload[3] != addressSize || payload.Length != 4 + addressSize)
        {
            throw new InvalidDataException("Invalid PostgreSQL address length.");
        }
        var value = new PgInet(ipv6 ? BinaryPrimitives.ReadUInt128BigEndian(payload[4..]) : BinaryPrimitives.ReadUInt32BigEndian(payload[4..]), payload[1], ipv6);
        // As in network_recv, the is_cidr byte is advisory; the selected type owns that decision.
        if (!IsValid(value, cidr))
        {
            throw new InvalidDataException("Invalid prefix or nonzero CIDR host bits.");
        }
        return value;
    }
}