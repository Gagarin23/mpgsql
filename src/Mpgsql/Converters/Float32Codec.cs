using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Mpgsql.Types;

namespace Mpgsql.Converters;

internal readonly struct Float32Codec : IBinaryCodec<float>
{
    public static uint Oid => (uint)TypeOid.Float32;
    public static int FixedSize => 4;
    public static bool MayOverlap => false;
    public static int Measure(float value)
    {
        return 4;
    }
    public static void CheckOverlap(float value, Span<byte> destination) { }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Write(float value, Span<byte> destination)
    {
        BinaryPrimitives.WriteSingleBigEndian(destination, value);
        return FixedSize;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Read(ReadOnlySpan<byte> payload)
    {
        BinaryPayload.RequireLength(payload.Length, 4);
        var value = BinaryPrimitives.ReadSingleBigEndian(payload);

        return value;
    }
    public static float Read(ReadOnlySequence<byte> payload) => payload.IsSingleSegment ? Read(payload.FirstSpan) : BitConverter.Int32BitsToSingle(unchecked((int)BinaryPayload.ReadUnsigned(payload, 4)));

}