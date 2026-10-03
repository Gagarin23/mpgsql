using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Mpgsql.Types;

namespace Mpgsql.Converters;

internal readonly struct BooleanCodec : IBinaryCodec<bool>
{
    public static uint Oid => (uint)TypeOid.Boolean;
    public static int FixedSize => 1;
    public static bool MayOverlap => false;
    public static int Measure(bool value)
    {
        return 1;
    }
    public static void CheckOverlap(bool value, Span<byte> destination) { }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Write(bool value, Span<byte> destination)
    {
        destination[0] = value ? (byte)1 : (byte)0;
        return FixedSize;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Read(ReadOnlySpan<byte> payload)
    {
        BinaryPayload.RequireLength(payload.Length, 1);
        var value = payload[0] != 0;
        if (!(payload[0] <= 1))
        {
            throw new InvalidDataException("Invalid binary boolean.");
        }
        return value;
    }
    public static bool Read(ReadOnlySequence<byte> payload) => BinaryPayload.ReadSmall<bool, BooleanCodec>(payload, 1);

}