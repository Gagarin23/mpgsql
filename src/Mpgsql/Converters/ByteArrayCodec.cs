using System.Buffers;

namespace Mpgsql.Converters;

internal readonly struct ByteArrayCodec : IBinaryCodec<byte[]>
{
    public static uint Oid => (uint)TypeOid.Bytea;
    public static int FixedSize => 0;
    public static bool NeedsValidation => false;
    public static bool MayOverlap => true;
    public static int Measure(byte[] value) => value.Length;
    public static void CheckOverlap(byte[] value, Span<byte> destination) => BinaryPayload.RequireSeparate(value, destination);
    public static void Write(byte[] value, Span<byte> destination) => value.AsSpan().CopyTo(destination);
    public static byte[] Read(ReadOnlySpan<byte> payload) => payload.ToArray();
    public static byte[] Read(ReadOnlySequence<byte> payload) => payload.ToArray();
}