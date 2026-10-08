using System.Buffers;

namespace Mpgsql.Converters;

internal readonly struct NameCodec : IBinaryCodec<string>
{
    public static uint Oid => (uint)TypeOid.Name;
    public static int FixedSize => 0;
    public static bool MayOverlap => false;
    public static int Measure(string value)
    {
        return Utf8Payload.Measure(value);
    }
    public static void CheckOverlap(string value, Span<byte> destination) { }
    public static int Write(string value, Span<byte> destination)
    {
        return Utf8Payload.Write(value, destination);
    }
    public static string Read(ReadOnlySpan<byte> payload)
    {
        return Utf8Payload.Read(payload);
    }
    public static string Read(ReadOnlySequence<byte> payload)
    {
        return Utf8Payload.Read(payload);
    }
}