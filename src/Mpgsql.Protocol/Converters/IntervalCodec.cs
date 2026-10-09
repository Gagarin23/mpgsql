using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using Mpgsql.Types;

namespace Mpgsql.Converters;

internal readonly struct IntervalCodec : IBinaryCodec<PgInterval>
{
    public static uint Oid => (uint)TypeOid.Interval;
    public static int FixedSize => 16;
    public static bool MayOverlap => false;
    public static int Measure(PgInterval value)
    {
        return 16;
    }
    public static void CheckOverlap(PgInterval value, Span<byte> destination) { }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Write(PgInterval value, Span<byte> destination)
    {
        BinaryPrimitives.WriteInt64BigEndian(destination, value.Microseconds);
        BinaryPrimitives.WriteInt32BigEndian(destination[8..], value.Days);
        BinaryPrimitives.WriteInt32BigEndian(destination[12..], value.Months);
        return FixedSize;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PgInterval Read(ReadOnlySpan<byte> payload)
    {
        BinaryPayload.RequireLength(payload.Length, 16);
        if (BitConverter.IsLittleEndian && (Ssse3.IsSupported || AdvSimd.Arm64.IsSupported) && Unsafe.SizeOf<PgInterval>() == 16)
        {
            // Wire: microseconds BE8, days BE4, months BE4. PgInterval's
            // sequential managed layout is months LE4, days LE4, microseconds LE8.
            // A complete 16-byte reversal changes both byte order and field order.
            var wire = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(payload));
            var reversed = Vector128.Shuffle(wire, Vector128.Create((byte)15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0));
            return Unsafe.BitCast<Vector128<byte>, PgInterval>(reversed);
        }
        var value = new PgInterval(BinaryPrimitives.ReadInt32BigEndian(payload[12..]), BinaryPrimitives.ReadInt32BigEndian(payload[8..]), BinaryPrimitives.ReadInt64BigEndian(payload));

        return value;
    }
    public static PgInterval Read(ReadOnlySequence<byte> payload)
    {
        return BinaryPayload.ReadSmall<PgInterval, IntervalCodec>(payload, 16);
    }

}
