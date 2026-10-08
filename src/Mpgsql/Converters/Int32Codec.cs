using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Mpgsql.Converters;

internal readonly struct Int32Codec : IBinaryCodec<int>
{
    public static uint Oid => (uint)TypeOid.Int32;
    public static int FixedSize => 4;
    public static bool MayOverlap => false;
    public static int Measure(int value)
    {
        return 4;
    }
    public static void CheckOverlap(int value, Span<byte> destination) { }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Write(int value, Span<byte> destination)
    {
        BinaryPrimitives.WriteInt32BigEndian(destination, value);
        return FixedSize;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Read(ReadOnlySpan<byte> payload)
    {
        BinaryPayload.RequireLength(payload.Length, 4);
        var value = BinaryPrimitives.ReadInt32BigEndian(payload);

        return value;
    }
    public static int Read(ReadOnlySequence<byte> payload)
    {
        return payload.IsSingleSegment ? Read(payload.FirstSpan) : unchecked((int)BinaryPayload.ReadUnsigned(payload, 4));
    }

}