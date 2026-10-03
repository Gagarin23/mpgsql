using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Mpgsql.Types;

namespace Mpgsql.Converters;

internal readonly struct OidCodec : IBinaryCodec<uint>
{
    public static uint Oid => (uint)TypeOid.Oid;
    public static int FixedSize => 4;
    public static bool MayOverlap => false;
    public static int Measure(uint value)
    {
        return 4;
    }
    public static void CheckOverlap(uint value, Span<byte> destination) { }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Write(uint value, Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt32BigEndian(destination, value);
        return FixedSize;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Read(ReadOnlySpan<byte> payload)
    {
        BinaryPayload.RequireLength(payload.Length, 4);
        var value = BinaryPrimitives.ReadUInt32BigEndian(payload);

        return value;
    }
    public static uint Read(ReadOnlySequence<byte> payload) => payload.IsSingleSegment ? Read(payload.FirstSpan) : unchecked((uint)BinaryPayload.ReadUnsigned(payload, 4));

}