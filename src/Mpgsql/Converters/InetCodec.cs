using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

internal readonly struct InetCodec : IBinaryCodec<PgInet>
{
    public static uint Oid => (uint)TypeOid.Inet;
    public static int FixedSize => 0;
    public static bool NeedsValidation => true;
    public static bool MayOverlap => false;
    public static int Measure(PgInet value) => NetworkPayload.Measure(value, false);
    public static void CheckOverlap(PgInet value, Span<byte> destination) { }
    public static void Write(PgInet value, Span<byte> destination) => NetworkPayload.Write(value, destination, false);
    public static PgInet Read(ReadOnlySpan<byte> payload) => NetworkPayload.Read(payload, false);
    public static PgInet Read(ReadOnlySequence<byte> payload)
    {
        if (payload.Length is not (8 or 20))
        {
            throw new InvalidDataException("Invalid network payload length.");
        }
        return BinaryPayload.ReadSmall<PgInet, InetCodec>(payload, (int)payload.Length);
    }
}