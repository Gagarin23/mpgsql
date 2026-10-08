using System.Buffers;
using System.Runtime.CompilerServices;

namespace Mpgsql.Converters;

internal readonly struct Int64Codec : IBinaryCodec<long>
{
    public static uint Oid => Int64Converter.TypeOid;
    public static int FixedSize => sizeof(long);
    public static bool MayOverlap => false;
    public static int Measure(long value) => FixedSize;
    public static void CheckOverlap(long value, Span<byte> destination) { }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Write(long value, Span<byte> destination) => Int64Converter.Write(value, destination);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Read(ReadOnlySpan<byte> payload) => Int64Converter.Read(payload);
    public static long Read(ReadOnlySequence<byte> payload) => Int64Converter.Read(payload);
}
