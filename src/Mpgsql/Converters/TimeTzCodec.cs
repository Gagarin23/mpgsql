using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Mpgsql.Types;

namespace Mpgsql.Converters;

internal readonly struct TimeTzCodec : IBinaryCodec<PgTimeTz>
{
    public static uint Oid => (uint)TypeOid.TimeTz;
    public static int FixedSize => 12;
    public static bool NeedsValidation => true;
    public static bool MayOverlap => false;
    public static int Measure(PgTimeTz value)
    {
        if (!IsValid(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Value is outside PostgreSQL's range.");
        }
        return 12;
    }
    public static void CheckOverlap(PgTimeTz value, Span<byte> destination) { }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(PgTimeTz value, Span<byte> destination)
    {
        BinaryPrimitives.WriteInt64BigEndian(destination, value.Time.Microseconds);
        BinaryPrimitives.WriteInt32BigEndian(destination[8..], -value.OffsetSeconds);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PgTimeTz Read(ReadOnlySpan<byte> payload)
    {
        BinaryPayload.RequireLength(payload.Length, 12);
        var value = new PgTimeTz(new PgTime(BinaryPrimitives.ReadInt64BigEndian(payload)), unchecked(-BinaryPrimitives.ReadInt32BigEndian(payload[8..])));
        if (!IsValid(value))
        {
            throw new InvalidDataException("Value is outside PostgreSQL's range.");
        }
        return value;
    }
    public static PgTimeTz Read(ReadOnlySequence<byte> payload) => BinaryPayload.ReadSmall<PgTimeTz, TimeTzCodec>(payload, 12);
    private static bool IsValid(PgTimeTz value) => (ulong)value.Time.Microseconds <= PgTime.MicrosecondsPerDay && value.OffsetSeconds is > -57600 and < 57600;
}