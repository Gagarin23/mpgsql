using System.Buffers;
using System.Runtime.CompilerServices;

namespace Mpgsql.Converters;

internal readonly struct UuidCodec : IBinaryCodec<Guid>
{
    public static uint Oid => (uint)TypeOid.Uuid;
    public static int FixedSize => 16;
    public static bool MayOverlap => false;
    public static int Measure(Guid value)
    {
        return 16;
    }
    public static void CheckOverlap(Guid value, Span<byte> destination) { }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Write(Guid value, Span<byte> destination)
    {
        value.TryWriteBytes(destination, true, out _);
        return FixedSize;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Guid Read(ReadOnlySpan<byte> payload)
    {
        BinaryPayload.RequireLength(payload.Length, 16);
        var value = new Guid(payload, true);

        return value;
    }
    public static Guid Read(ReadOnlySequence<byte> payload)
    {
        return BinaryPayload.ReadSmall<Guid, UuidCodec>(payload, 16);
    }

}