using System.Buffers;

namespace Mpgsql.Converters;

internal readonly struct ByteArrayCodec : IBinaryCodec<byte[]>
{
    public static uint Oid => (uint)TypeOid.Bytea;
    public static int FixedSize => 0;
    public static bool MayOverlap => true;
    public static int Measure(byte[] value)
    {
        return value.Length;
    }
    public static void CheckOverlap(byte[] value, Span<byte> destination)
    {
        BinaryPayload.RequireSeparate(value, destination);
    }
    public static int Write(byte[] value, Span<byte> destination)
    {
        value
            .AsSpan()
            .CopyTo(destination);
        return value.Length;
    }
    public static byte[] Read(ReadOnlySpan<byte> payload)
    {
        return payload.ToArray();
    }
    public static byte[] Read(ReadOnlySequence<byte> payload)
    {
        return payload.ToArray();
    }
}