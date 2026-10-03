using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
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
        var value = new PgInterval(BinaryPrimitives.ReadInt32BigEndian(payload[12..]), BinaryPrimitives.ReadInt32BigEndian(payload[8..]), BinaryPrimitives.ReadInt64BigEndian(payload));

        return value;
    }
    public static PgInterval Read(ReadOnlySequence<byte> payload) => BinaryPayload.ReadSmall<PgInterval, IntervalCodec>(payload, 16);

}