using System.Buffers;

namespace Mpgsql.Converters;

internal readonly struct XmlCodec : IBinaryCodec<string>
{
    public static uint Oid => (uint)TypeOid.Xml;
    public static int FixedSize => 0;
    public static bool MayOverlap => false;
    public static int Measure(string value) => Utf8Payload.Measure(value);
    public static void CheckOverlap(string value, Span<byte> destination) { }
    public static int Write(string value, Span<byte> destination) => Utf8Payload.Write(value, destination);
    public static string Read(ReadOnlySpan<byte> payload) => Utf8Payload.Read(payload);
    public static string Read(ReadOnlySequence<byte> payload) => Utf8Payload.Read(payload);
}
