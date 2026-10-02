using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Mpgsql.Types;

namespace Mpgsql.Converters;

internal readonly struct Float64Codec : IBinaryCodec<double>
{
    public static uint Oid => (uint)TypeOid.Float64;
    public static int FixedSize => 8;
    public static bool NeedsValidation => false;
    public static bool MayOverlap => false;
    public static int Measure(double value)
    {
        return 8;
    }
    public static void CheckOverlap(double value, Span<byte> destination) { }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(double value, Span<byte> destination)
    {
        BinaryPrimitives.WriteDoubleBigEndian(destination, value);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Read(ReadOnlySpan<byte> payload)
    {
        BinaryPayload.RequireLength(payload.Length, 8);
        var value = BinaryPrimitives.ReadDoubleBigEndian(payload);

        return value;
    }
    public static double Read(ReadOnlySequence<byte> payload) => payload.IsSingleSegment ? Read(payload.FirstSpan) : BitConverter.Int64BitsToDouble(unchecked((long)BinaryPayload.ReadUnsigned(payload, 8)));

}