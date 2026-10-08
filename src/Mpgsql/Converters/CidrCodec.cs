using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

internal readonly struct CidrCodec : IBinaryCodec<PgInet>
{
    public static uint Oid => (uint)TypeOid.Cidr;
    public static int FixedSize => 0;
    public static bool MayOverlap => false;
    public static int Measure(PgInet value)
    {
        return NetworkPayload.Measure(value, true);
    }
    public static void CheckOverlap(PgInet value, Span<byte> destination) { }
    public static int Write(PgInet value, Span<byte> destination)
    {
        return NetworkPayload.Write(value, destination, true);
    }
    public static PgInet Read(ReadOnlySpan<byte> payload)
    {
        return NetworkPayload.Read(payload, true);
    }
    public static PgInet Read(ReadOnlySequence<byte> payload)
    {
        if (payload.Length is not (8 or 20))
        {
            throw new InvalidDataException("Invalid network payload length.");
        }
        return BinaryPayload.ReadSmall<PgInet, CidrCodec>(payload, (int)payload.Length);
    }
}