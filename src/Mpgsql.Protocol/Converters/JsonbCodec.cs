using System.Buffers;

namespace Mpgsql.Converters;

internal readonly struct JsonbCodec : IBinaryCodec<Memory<byte>>
{
    public static uint Oid => (uint)TypeOid.Jsonb;
    public static int FixedSize => 0;
    public static bool MayOverlap => true;
    public static int Measure(Memory<byte> value)
    {
        return checked(value.Length + 1);
    }
    public static void CheckOverlap(Memory<byte> value, Span<byte> destination)
    {
        BinaryPayload.RequireSeparate(value.Span, destination);
    }
    public static int Write(Memory<byte> value, Span<byte> destination)
    {
        destination[0] = 1;
        value.Span.CopyTo(destination[1..]);
        return value.Length + 1;
    }
    public static Memory<byte> Read(ReadOnlySpan<byte> payload)
    {
        return Utf8Payload
            .ReadUtf8(payload, true)
            .ToArray();
    }
    public static Memory<byte> Read(ReadOnlySequence<byte> payload)
    {
        return Utf8Payload
            .ReadUtf8(payload, true)
            .ToArray();
    }
}