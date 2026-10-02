using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Mpgsql.Types;

namespace Mpgsql.Converters;

internal readonly struct TimestampCodec : IBinaryCodec<PgTimestamp>
{
    public static uint Oid => (uint)TypeOid.Timestamp;
    public static int FixedSize => 8;
    public static bool NeedsValidation => true;
    public static bool MayOverlap => false;
    public static int Measure(PgTimestamp value)
    {
        if (!IsValid(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Value is outside PostgreSQL's range.");
        }
        return 8;
    }
    public static void CheckOverlap(PgTimestamp value, Span<byte> destination) { }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(PgTimestamp value, Span<byte> destination)
    {
        BinaryPrimitives.WriteInt64BigEndian(destination, value.MicrosecondsSinceEpoch);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PgTimestamp Read(ReadOnlySpan<byte> payload)
    {
        BinaryPayload.RequireLength(payload.Length, 8);
        var value = new PgTimestamp(BinaryPrimitives.ReadInt64BigEndian(payload));
        if (!IsValid(value))
        {
            throw new InvalidDataException("Value is outside PostgreSQL's range.");
        }
        return value;
    }
    public static PgTimestamp Read(ReadOnlySequence<byte> payload) => BinaryPayload.ReadSmall<PgTimestamp, TimestampCodec>(payload, 8);
    private static bool IsValid(PgTimestamp value) => !value.IsFinite || value.MicrosecondsSinceEpoch is >= PgTimestamp.MinFiniteMicroseconds and <= PgTimestamp.MaxFiniteMicroseconds;
}