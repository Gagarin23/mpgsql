using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Mpgsql.Types;

namespace Mpgsql.Converters;

internal readonly struct TimestampTzCodec : IBinaryCodec<PgTimestampTz>
{
    public static uint Oid => (uint)TypeOid.TimestampTz;
    public static int FixedSize => 8;
    public static bool NeedsValidation => true;
    public static bool MayOverlap => false;
    public static int Measure(PgTimestampTz value)
    {
        if (!IsValid(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Value is outside PostgreSQL's range.");
        }
        return 8;
    }
    public static void CheckOverlap(PgTimestampTz value, Span<byte> destination) { }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(PgTimestampTz value, Span<byte> destination)
    {
        BinaryPrimitives.WriteInt64BigEndian(destination, value.MicrosecondsSinceEpoch);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PgTimestampTz Read(ReadOnlySpan<byte> payload)
    {
        BinaryPayload.RequireLength(payload.Length, 8);
        var value = new PgTimestampTz(BinaryPrimitives.ReadInt64BigEndian(payload));
        if (!IsValid(value))
        {
            throw new InvalidDataException("Value is outside PostgreSQL's range.");
        }
        return value;
    }
    public static PgTimestampTz Read(ReadOnlySequence<byte> payload) => BinaryPayload.ReadSmall<PgTimestampTz, TimestampTzCodec>(payload, 8);
    private static bool IsValid(PgTimestampTz value) => !value.IsFinite || value.MicrosecondsSinceEpoch is >= PgTimestamp.MinFiniteMicroseconds and <= PgTimestamp.MaxFiniteMicroseconds;
}