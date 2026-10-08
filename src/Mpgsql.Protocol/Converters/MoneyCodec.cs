using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Mpgsql.Converters;

internal readonly struct MoneyCodec : IBinaryCodec<long>
{
    public static uint Oid => (uint)TypeOid.Money;
    public static int FixedSize => 8;
    public static bool MayOverlap => false;
    public static int Measure(long value)
    {
        return 8;
    }
    public static void CheckOverlap(long value, Span<byte> destination) { }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Write(long value, Span<byte> destination)
    {
        BinaryPrimitives.WriteInt64BigEndian(destination, value);
        return FixedSize;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Read(ReadOnlySpan<byte> payload)
    {
        BinaryPayload.RequireLength(payload.Length, 8);
        var value = BinaryPrimitives.ReadInt64BigEndian(payload);

        return value;
    }
    public static long Read(ReadOnlySequence<byte> payload)
    {
        return payload.IsSingleSegment ? Read(payload.FirstSpan) : unchecked((long)BinaryPayload.ReadUnsigned(payload, 8));
    }

}