using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Mpgsql.Types;

namespace Mpgsql.Converters;

internal readonly struct Int16Codec : IBinaryCodec<short>
{
    public static uint Oid => (uint)TypeOid.Int16;
    public static int FixedSize => 2;
    public static bool MayOverlap => false;
    public static int Measure(short value)
    {
        return 2;
    }
    public static void CheckOverlap(short value, Span<byte> destination) { }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Write(short value, Span<byte> destination)
    {
        BinaryPrimitives.WriteInt16BigEndian(destination, value);
        return FixedSize;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static short Read(ReadOnlySpan<byte> payload)
    {
        BinaryPayload.RequireLength(payload.Length, 2);
        var value = BinaryPrimitives.ReadInt16BigEndian(payload);

        return value;
    }
    public static short Read(ReadOnlySequence<byte> payload) => payload.IsSingleSegment ? Read(payload.FirstSpan) : unchecked((short)BinaryPayload.ReadUnsigned(payload, 2));

}