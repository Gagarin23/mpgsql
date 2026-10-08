using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Mpgsql.Types;

namespace Mpgsql.Converters;

internal readonly struct DateCodec : IBinaryCodec<PgDate>
{
    public static uint Oid => (uint)TypeOid.Date;
    public static int FixedSize => 4;
    public static bool MayOverlap => false;
    public static int Measure(PgDate value)
    {
        if (!IsValid(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Value is outside PostgreSQL's range.");
        }
        return 4;
    }
    public static void CheckOverlap(PgDate value, Span<byte> destination) { }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Write(PgDate value, Span<byte> destination)
    {
        BinaryPrimitives.WriteInt32BigEndian(destination, value.DaysSinceEpoch);
        return FixedSize;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PgDate Read(ReadOnlySpan<byte> payload)
    {
        BinaryPayload.RequireLength(payload.Length, 4);
        var value = new PgDate(BinaryPrimitives.ReadInt32BigEndian(payload));
        if (!IsValid(value))
        {
            throw new InvalidDataException("Value is outside PostgreSQL's range.");
        }
        return value;
    }
    public static PgDate Read(ReadOnlySequence<byte> payload)
    {
        return BinaryPayload.ReadSmall<PgDate, DateCodec>(payload, 4);
    }
    private static bool IsValid(PgDate value)
    {
        return !value.IsFinite || value.DaysSinceEpoch is >= PgDate.MinFiniteDays and <= PgDate.MaxFiniteDays;
    }
}