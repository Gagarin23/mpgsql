using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Mpgsql.Types;

namespace Mpgsql.Converters;

internal readonly struct TimeCodec : IBinaryCodec<PgTime>
{
    public static uint Oid => (uint)TypeOid.Time;
    public static int FixedSize => 8;
    public static bool MayOverlap => false;
    public static int Measure(PgTime value)
    {
        if (!IsValid(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Value is outside PostgreSQL's range.");
        }
        return 8;
    }
    public static void CheckOverlap(PgTime value, Span<byte> destination) { }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Write(PgTime value, Span<byte> destination)
    {
        BinaryPrimitives.WriteInt64BigEndian(destination, value.Microseconds);
        return FixedSize;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PgTime Read(ReadOnlySpan<byte> payload)
    {
        BinaryPayload.RequireLength(payload.Length, 8);
        var value = new PgTime(BinaryPrimitives.ReadInt64BigEndian(payload));
        if (!IsValid(value))
        {
            throw new InvalidDataException("Value is outside PostgreSQL's range.");
        }
        return value;
    }
    public static PgTime Read(ReadOnlySequence<byte> payload) => BinaryPayload.ReadSmall<PgTime, TimeCodec>(payload, 8);
    private static bool IsValid(PgTime value) => (ulong)value.Microseconds <= PgTime.MicrosecondsPerDay;
}